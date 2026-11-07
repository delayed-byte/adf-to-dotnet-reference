using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestHarness.Orchestration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.UnitTests.Master;

/// <summary>
/// TR_Nightly_0300_Pacific as the Functions host invokes it (ADR 0016): the fire time comes from the injected TimeProvider, each
/// invocation starts exactly one master under a new GUID, and the input is the previous Pacific day of the occurrence.
/// AT-18 drives the same function through the timer emulator across both DST changes.
/// </summary>
public sealed class NightlyTriggerTests : IDisposable
{
    private readonly MasterWorld world = new();
    private readonly RecordingDurableTaskClient client;

    public NightlyTriggerTests()
    {
        client = new RecordingDurableTaskClient(world.Clock);
    }

    public void Dispose() => world.Dispose();

    [Theory]
    [InlineData("2026-03-08T10:00:01.5Z", "2026-03-07", "2026-03-08T10:00:00Z")]
    [InlineData("2026-03-09T10:00:01.5Z", "2026-03-08", "2026-03-09T10:00:00Z")]
    [InlineData("2026-11-01T11:00:01.5Z", "2026-10-31", "2026-11-01T11:00:00Z")]
    [InlineData("2026-11-02T11:00:01.5Z", "2026-11-01", "2026-11-02T11:00:00Z")]
    [InlineData("2026-11-05T10:59:59.9Z", "2026-11-04", "2026-11-05T11:00:00Z")]
    public async Task One_invocation_starts_one_master_for_the_previous_Pacific_day_of_its_occurrence(string firedUtc, string businessDate, string scheduledUtc)
    {
        world.Clock.Set(DateTimeOffset.Parse(firedUtc, System.Globalization.CultureInfo.InvariantCulture));

        await world.Host.InvokeTimerAsync(TriggerNames.Nightly, new TimerInfo(), client, TestContext.Current.CancellationToken);

        var started = client.Scheduled.ShouldHaveSingleItem();
        started.Name.ShouldBe(PipelineNames.Master);
        Guid.TryParseExact(started.InstanceId, "D", out _).ShouldBeTrue("the instance ID is the batchRunId, a GUID (ADR 0008)");
        var input = world.Host.Converter.Deserialize<NightlyBatchInput>(started.SerializedInput).ShouldNotBeNull();
        input.BusinessDate.ShouldBe(DateOnly.Parse(businessDate, System.Globalization.CultureInfo.InvariantCulture));
        input.TriggerName.ShouldBe(TriggerNames.Nightly);
        input.TriggerTimeUtc.ShouldBe(DateTimeOffset.Parse(scheduledUtc, System.Globalization.CultureInfo.InvariantCulture));
        (input.ReconTolerance, input.RetainUntil).ShouldBe((0.00m, "TBD"));
        world.Logs.Logs.ShouldContain(l => l.Level == LogLevel.Information && l.Message.Contains(started.InstanceId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_invocation_gets_a_new_batch_run_ID()
    {
        world.Clock.Set(new DateTimeOffset(2026, 11, 5, 11, 0, 1, TimeSpan.Zero));

        await world.Host.InvokeTimerAsync(TriggerNames.Nightly, new TimerInfo(), client, TestContext.Current.CancellationToken);
        await world.Host.InvokeTimerAsync(TriggerNames.Nightly, new TimerInfo(), client, TestContext.Current.CancellationToken);

        client.Scheduled.Select(s => s.InstanceId).Distinct().Count().ShouldBe(2, "a duplicate invocation starts a second, separately audited master (ADR 0016)");
    }

    [Fact]
    public async Task A_past_due_invocation_runs_the_missed_occurrences_date_and_says_so()
    {
        world.Clock.Set(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));

        await world.Host.InvokeTimerAsync(TriggerNames.Nightly, new TimerInfo { IsPastDue = true }, client, TestContext.Current.CancellationToken);

        var input = world.Host.Converter.Deserialize<NightlyBatchInput>(client.Scheduled.ShouldHaveSingleItem().SerializedInput)!;
        input.BusinessDate.ShouldBe(new DateOnly(2026, 11, 4), "the Nov 5 03:00 occurrence was missed; it processes Nov 4");
        input.TriggerTimeUtc.ShouldBe(new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero));
        world.Logs.Logs.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.Contains("past due", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_master_the_trigger_started_runs_for_the_triggers_business_date()
    {
        world.Clock.Set(new DateTimeOffset(2026, 11, 1, 11, 0, 1, TimeSpan.Zero));
        await world.Host.InvokeTimerAsync(TriggerNames.Nightly, new TimerInfo(), client, TestContext.Current.CancellationToken);

        var result = await world.Host.RunScheduledAsync(client.Scheduled.Single());

        result.Succeeded.ShouldBeTrue(result.Describe());
        world.Store.Events.ShouldAllBe(e => e.BusinessDate == new DateOnly(2026, 10, 31) && e.BatchRunId == Guid.Parse(client.Scheduled.Single().InstanceId));
        world.Store.Events[0].Notes.ShouldBe("trigger=TR_Nightly_0300_Pacific; triggerTimeUtc=2026-11-01T11:00:00.0000000Z");
    }
}
