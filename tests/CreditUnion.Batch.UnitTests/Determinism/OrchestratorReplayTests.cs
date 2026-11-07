// The control orchestrators below call activities ("Log", "C") that exist only in the replay backend's script.
#pragma warning disable DURABLE2003

using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.Functions.Activities;
using CreditUnion.Batch.UnitTests.Extract;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Converters;
using Microsoft.DurableTask.Protobuf;

namespace CreditUnion.Batch.UnitTests.Determinism;

/// <summary>
/// The PRD's determinism check (risk table: "a unit test that replays each orchestrator", ADR 0017). The in-process host does not
/// replay (ADR 0008), so these tests use the isolated worker's real replay engine: every orchestrator of the batch is run to
/// completion episode by episode through <see cref="ReplayBackend"/>, which records genuine Durable histories, and every episode
/// is then replayed and must return exactly the same actions. Negative controls show the check catches a wall-clock read and a
/// branch on state that changes between executions.
/// </summary>
public sealed class OrchestratorReplayTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 11, 0, 0, DateTimeKind.Utc);
    private static readonly Guid RunId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DataConverter Converter = JsonDataConverter.Default;

    [Fact]
    public async Task Every_orchestrator_of_a_successful_batch_replays_to_the_same_actions()
    {
        var backend = ReplayBackend.ForFunctionsApp((name, input, attempt) => Batch(name, input, attempt, mapFails: false), Start);

        var master = backend.Run(PipelineNames.Master, RunId.ToString("D"), new NightlyBatchInput { BusinessDate = new DateOnly(2026, 11, 4) });

        master.Succeeded.ShouldBeTrue(master.Completion?.FailureDetails?.ErrorMessage);
        backend.Recorded.Select(r => r.Name).ShouldBe(
            [PipelineNames.Master, PipelineNames.Extract, PipelineNames.CardSettlement, PipelineNames.MapAndAggregate, PipelineNames.Reconciliation], ignoreOrder: true);
        backend.Recorded.ShouldAllBe(r => r.Episodes.Count >= 4, "each orchestrator ran (and was replayed) over several episodes");
        HasTimer(backend.Recorded.Single(r => r.Name == PipelineNames.Reconciliation)).ShouldBeTrue("the retried webhook POST put a durable timer into the history");

        await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken); // the replays run at a different wall-clock time
        backend.Recorded.SelectMany(r => ReplayCheck.Differences(backend, r)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_orchestrator_of_a_failed_batch_with_retries_replays_to_the_same_actions()
    {
        var backend = ReplayBackend.ForFunctionsApp((name, input, attempt) => Batch(name, input, attempt, mapFails: true), Start);

        var master = backend.Run(PipelineNames.Master, RunId.ToString("D"), new NightlyBatchInput { BusinessDate = new DateOnly(2026, 11, 4) });

        master.Succeeded.ShouldBeFalse();
        master.Completion!.FailureDetails.ErrorType.ShouldBe(typeof(PipelineFailedException).FullName);
        master.Completion.FailureDetails.ErrorMessage.ShouldBe("BATCH_STEP_FAILED: PL_MapAndAggregate failed: Unmapped transaction codes: P2PD",
            "the real engine carries the child's code through the failure, as the in-process host does");
        backend.Recorded.Select(r => r.Name).ShouldNotContain(PipelineNames.Reconciliation);
        HasTimer(backend.Recorded.Single(r => r.Name == PipelineNames.Extract)).ShouldBeTrue("the copy failed twice and was retried after durable timers");
        var failedEvent = backend.Recorded.Single(r => r.Name == PipelineNames.Master).Episodes.SelectMany(e => e.Response.Actions)
            .Where(a => a.ScheduleTask?.Name == MasterActivityNames.LogEvent).Select(a => Argument<Core.Extract.LogBatchRunEventRequest>(a.ScheduleTask.Input))
            .Single(e => e.Status == "Failed");
        (failedEvent.StepName, failedEvent.ErrorCode).ShouldBe((PipelineNames.MapAndAggregate, PipelineErrorCodes.TxnCodeUnmapped));

        await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
        backend.Recorded.SelectMany(r => ReplayCheck.Differences(backend, r)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_check_catches_an_orchestrator_that_puts_the_wall_clock_into_an_activity_input()
    {
        var backend = new ReplayBackend((_, _, _) => ActivityOutcome.Returns(null), Start);
#pragma warning disable DURABLE0001 // The negative control reads the wall clock on purpose.
        backend.Register<object>("WallClock", async context => await context.CallActivityAsync("Log", DateTime.UtcNow.Ticks));
#pragma warning restore DURABLE0001

        var recorded = backend.Run("WallClock", "wall-clock", null);
        await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);

        ReplayCheck.Differences(backend, recorded).ShouldNotBeEmpty("a replay at another time schedules the activity with another input");
    }

    [Fact]
    public void The_check_catches_an_orchestrator_whose_branch_changes_between_executions()
    {
        var executions = 0;
        var backend = new ReplayBackend((_, _, _) => ActivityOutcome.Returns(null), Start);
        backend.Register<object>("Branching", async context =>
        {
            // Stands for I/O or shared state read inside the orchestrator: the answer differs on the replay.
            await context.CallActivityAsync(Interlocked.Increment(ref executions) == 1 ? "A" : "B");
            await context.CallActivityAsync("C");
        });

        var recorded = backend.Run("Branching", "branching", null);

        recorded.Succeeded.ShouldBeFalse("the backend's own episodes already replay, and the engine rejects the history mismatch");
        recorded.Completion!.FailureDetails.ErrorType.ShouldContain("NonDeterministic");
    }

    [Fact]
    public async Task An_orchestrator_that_uses_only_context_time_and_context_GUIDs_passes()
    {
        var backend = new ReplayBackend((_, _, _) => ActivityOutcome.Returns(null), Start);
        backend.Register<object>("Deterministic", async context =>
        {
            await context.CallActivityAsync("Log", new { context.CurrentUtcDateTime, Id = context.NewGuid() });
            await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(30), CancellationToken.None);
            await context.CallActivityAsync("Log", new { context.CurrentUtcDateTime, Id = context.NewGuid() });
        });

        var recorded = backend.Run("Deterministic", "deterministic", null);
        await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);

        recorded.Succeeded.ShouldBeTrue();
        ReplayCheck.Differences(backend, recorded).ShouldBeEmpty();
    }

    private static bool HasTimer(RecordedOrchestration orchestration) =>
        orchestration.Episodes.SelectMany(e => e.Response.Actions).Any(a => a.OrchestratorActionTypeCase == OrchestratorAction.OrchestratorActionTypeOneofCase.CreateTimer);

    /// <summary>Scripted activity results for a whole batch: three tables, a valid and an invalid settlement file, a retried webhook POST.</summary>
    private static ActivityOutcome Batch(string name, string? input, int attempt, bool mapFails) => name switch
    {
        ExtractActivityNames.LookupConfig => ActivityOutcome.Returns(ExtractTestData.Tables(3)),
        ExtractActivityNames.CopyToStaging when mapFails && attempt <= 2 && input!.Contains("KsT2", StringComparison.Ordinal) =>
            ActivityOutcome.Fails<TimeoutException>("Copy Db2 To Staging exceeded its ADF timeout of 01:00:00."),
        ExtractActivityNames.CopyToStaging => ActivityOutcome.Returns(10L),
        CardActivityNames.ListLandingFiles => ActivityOutcome.Returns(new[]
        {
            new LandingItem("settlement_001.csv", LandingItem.FileType), new LandingItem("settlement_002.csv", LandingItem.FileType),
            new LandingItem("readme.txt", LandingItem.FileType), new LandingItem("old", LandingItem.FolderType),
        }),
        CardActivityNames.CopyToRawStaging => ActivityOutcome.Returns(2L),
        CardActivityNames.Validate => ActivityOutcome.Returns(Validation(Argument<SettlementFileRequest>(input).FileName)),
        MapActivityNames.GetGlMapping => ActivityOutcome.Returns(new KeyBridgeGlMappings("2026-11-01", "[]")),
        MapActivityNames.LookupUnmapped => ActivityOutcome.Returns(mapFails ? new UnmappedTxnCodes(1, "P2PD") : new UnmappedTxnCodes(0, string.Empty)),
        MapActivityNames.Aggregate => ActivityOutcome.Returns(9),
        ReconActivityNames.LookupCardTotals => ActivityOutcome.Returns(new CardReconTotals(10.00m, 9.99m, 0.01m, 0.01m, true)),
        ReconActivityNames.LookupGlTotals => ActivityOutcome.Returns(new GlReconTotals(2, 0, 0.00m, false)),
        ReconActivityNames.RecordExceptions => ActivityOutcome.Returns(new ReconExceptionResult(true, 1, 1, true, "CardSettlement ALL difference 0.01")),
        ReconActivityNames.PostAlert when attempt == 1 => ActivityOutcome.Fails<ReconAlertUnavailableException>("The reconciliation alert webhook answered 503."),
        MasterActivityNames.ExportEvidence => ActivityOutcome.Returns(new RunEvidenceExport($"evidence/2026-11-04/batch-run-{RunId:D}.json", 2)),
        ExtractActivityNames.LogEvent or ExtractActivityNames.Merge
            or CardActivityNames.LogEvent or CardActivityNames.ResetStaging or CardActivityNames.Promote or CardActivityNames.Archive
            or CardActivityNames.DeleteArchived or CardActivityNames.LogArchived or CardActivityNames.WriteManifest or CardActivityNames.Quarantine
            or CardActivityNames.DeleteQuarantined or CardActivityNames.LogQuarantined or CardActivityNames.Merge
            or MapActivityNames.LogEvent or MapActivityNames.CacheGlMapping or MapActivityNames.LogUnmapped
            or ReconActivityNames.LogEvent or ReconActivityNames.PostAlert or ReconActivityNames.MarkAlertSent
            or MasterActivityNames.LogEvent => ActivityOutcome.Returns(null),
        _ => throw new InvalidOperationException($"The replay script has no outcome for activity {name}."),
    };

    /// <summary>DurableTask.Core records an activity's input as a one-element JSON array of its parameters; the host unwraps it.</summary>
    private static T Argument<T>(string? input)
    {
        using var document = System.Text.Json.JsonDocument.Parse(input!);
        var argument = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? document.RootElement[0] : document.RootElement;
        return Converter.Deserialize<T>(argument.GetRawText())!;
    }

    private static SettlementValidation Validation(string file) => file == "settlement_002.csv"
        ? new SettlementValidation(file, false, 2, 3, 10.00m, 12.00m, 1, "Trailer record count 3 does not match 2 detail records.")
        : new SettlementValidation(file, true, 2, 2, 10.00m, 10.00m, 0, null);
}
