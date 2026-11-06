using System.Data;
using System.Globalization;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Orchestration;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.Master;

/// <summary>PL_Master_NightlyBatch and TR_Nightly_0300_Pacific: AT-14, AT-15, AT-16 and AT-18.</summary>
public sealed class NightlyBatchTests(BatchEnvironmentFixture fixture)
{
    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("AT", "AT-14")]
    public async Task AT14_the_happy_path_runs_Extract_and_Card_in_parallel_then_Map_then_Recon_and_writes_the_evidence_file()
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        Guid[] runIds = [Guid.NewGuid()];
        var expected = scenario.ExpectedRun(1);
        expected.Stages.Select(s => string.Join("+", s)).ShouldBe(["PL_ExtractKeyStone+PL_IngestCardSettlement", "PL_MapAndAggregate", "PL_Reconciliation"]);

        var run = await host.RunAsync(scenario.Runs[0], runIds[0]);

        run.ShouldMatchOutcome(expected);
        host.Trace.ShouldHaveRunInStages(run, expected);
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, cancellationToken: Ct);
        await GoldenAssert.BlobsMatchAsync(Env.Blobs, scenario, runIds, cancellationToken: Ct);
        scenario.Golden.Blobs.ShouldContain(b => b.Container == BatchOracle.EvidenceContainer && b.Name == "2026-11-04/batch-run-{run:1}.json", "the evidence file is written");
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(scenario.Runs[0], Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-15")]
    public async Task AT15_when_Map_fails_Reconciliation_does_not_run_and_BatchRun_records_the_failure_at_Map()
    {
        var scenario = Scenario.Load("map-unmapped-code");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        Guid[] runIds = [Guid.NewGuid()];
        var expected = scenario.ExpectedRun(1);
        (expected.ErrorCode, expected.FailedStep).ShouldBe((PipelineErrorCodes.BatchStepFailed, PipelineNames.MapAndAggregate));
        expected.PipelinesNotRun.ShouldBe([PipelineNames.Reconciliation]);

        var run = await host.RunAsync(scenario.Runs[0], runIds[0]);

        run.ShouldMatchOutcome(expected);
        host.Trace.ShouldHaveRunInStages(run, expected);
        (await MasterFailedEventAsync(runIds[0])).ShouldBe((PipelineNames.MapAndAggregate, PipelineErrorCodes.TxnCodeUnmapped),
            "the master's Failed event names the failing step and the child's error code (ADR 0007)");
        await Env.Reporting.ShouldHaveLoggedMessagesAsync(runIds[0], expected.Messages, Ct);
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, cancellationToken: Ct);
        await GoldenAssert.BlobsMatchAsync(Env.Blobs, scenario, runIds, cancellationToken: Ct);
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(scenario.Runs[0], Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-16")]
    public async Task AT16_a_rerun_for_an_earlier_business_date_affects_only_that_date()
    {
        var scenario = Scenario.Load("rerun-earlier-date");
        scenario.Runs.Select(r => r.BusinessDate).ShouldBe(["2026-11-03", "2026-11-04", "2026-11-03"]);
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        Guid[] runIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        var first = await host.RunAsync(scenario.Runs[0], runIds[0]);
        var second = await host.RunAsync(scenario.Runs[1], runIds[1]);
        var beforeRerun = await Idempotency.SnapshotAsync(host, runIds);
        var rerun = await host.RunAsync(scenario.Runs[2], runIds[2]);
        var afterRerun = await Idempotency.SnapshotAsync(host, runIds);

        foreach (var (run, n) in new[] { (first, 1), (second, 2), (rerun, 3) })
        {
            run.ShouldMatchOutcome(scenario.ExpectedRun(n));
        }

        Idempotency.ShouldBeUnchanged(beforeRerun, afterRerun, scenario, "rerunning 2026-11-03 changes no data, for either date");
        (await BusinessDatesLoggedAsync(runIds[2])).ShouldBe([new DateOnly(2026, 11, 3)], "the rerun's audit events are all for 2026-11-03");
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, cancellationToken: Ct);
        await GoldenAssert.BlobsMatchAsync(Env.Blobs, scenario, runIds, cancellationToken: Ct);
    }

    [Fact]
    [Trait("AT", "AT-18")]
    public async Task AT18_on_the_DST_change_days_the_trigger_fires_once_and_each_business_date_is_the_previous_Pacific_day()
    {
        var scenario = Scenario.Load("dst-transitions");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var trigger = host.Functions.Functions.Timer(TriggerNames.Nightly);
        var timer = TimerTriggerEmulator.For(trigger, TimerTriggerEmulator.ReadLocalSettings(RepositoryPaths.FunctionsLocalSettings));
        var client = new RecordingDurableTaskClient(host.Clock);
        var fired = new Dictionary<DateTimeOffset, ScheduledOrchestration>();

        // Drive the trigger the way the Functions host would, day by day across both 2026 DST changes.
        foreach (var day in scenario.Golden.Schedule!)
        {
            var localDate = DateOnly.ParseExact(day.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var occurrences = timer.Occurrences(StartOfDayUtc(localDate, timer.TimeZone), StartOfDayUtc(localDate.AddDays(1), timer.TimeZone));
            var fireTime = occurrences.ShouldHaveSingleItem($"the trigger fires exactly once on {day.LocalDate} ({timer.Expression} in {timer.TimeZone.Id})");
            Utc(fireTime).ShouldBe(day.FireTimeUtc, $"fire time on {day.LocalDate} ({day.FireTimeLocal})");

            var before = client.Scheduled.Count;
            host.Clock.Set(fireTime.AddSeconds(1.5));
            await host.Functions.InvokeTimerAsync(TriggerNames.Nightly, new TimerInfo(), client, Ct);
            var started = client.Scheduled.Skip(before).ShouldHaveSingleItem($"one master run started on {day.LocalDate}");
            started.Name.ShouldBe(PipelineNames.Master);
            var input = host.Functions.Converter.Deserialize<NightlyBatchInput>(started.SerializedInput).ShouldNotBeNull();
            input.BusinessDate.ShouldBe(DateOnly.ParseExact(day.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), $"businessDate for the {day.LocalDate} run");
            fired[fireTime] = started;
        }

        // Run the masters the trigger started on the scenario's days; the golden data checks where near-midnight transactions land.
        var runs = new List<PipelineRun>();
        foreach (var spec in scenario.Runs)
        {
            var fireTime = DateTimeOffset.Parse(spec.TriggerTimeUtc!, CultureInfo.InvariantCulture);
            runs.Add(await host.RunScheduledAsync(fired[fireTime], fireTime));
            runs[^1].ShouldMatchOutcome(scenario.ExpectedRun(spec.Run));
            runs[^1].BusinessDate.ShouldBe(spec.Date());
        }

        var runIds = runs.Select(r => r.BatchRunId).ToList();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, cancellationToken: Ct);
        await GoldenAssert.BlobsMatchAsync(Env.Blobs, scenario, runIds, cancellationToken: Ct);
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, runs, async () =>
        {
            var reruns = new List<PipelineRun>();
            foreach (var spec in scenario.Runs)
            {
                reruns.Add(await host.RunAsync(spec, Guid.NewGuid()));
            }

            return reruns;
        });
    }

    private static DateTimeOffset StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<(string? StepName, string? ErrorCode)> MasterFailedEventAsync(Guid batchRunId)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "SELECT StepName, ErrorCode FROM ops.BatchRun WHERE BatchRunId = @id AND PipelineName = 'PL_Master_NightlyBatch' AND Status = 'Failed'", connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue("ops.BatchRun has the master's Failed event");
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<List<DateOnly>> BusinessDatesLoggedAsync(Guid batchRunId)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand("SELECT DISTINCT BusinessDate FROM ops.BatchRun WHERE BatchRunId = @id", connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        var dates = new List<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            dates.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        }

        return dates;
    }
}
