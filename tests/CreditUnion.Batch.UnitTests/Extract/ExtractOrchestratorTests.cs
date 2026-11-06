using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Activities;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CreditUnion.Batch.UnitTests.Extract;

/// <summary>
/// PL_ExtractKeyStone as the in-process Durable host runs it (ADR 0008), with the reporting store and the
/// Db2 copier replaced by fakes: activity order, parallelism bounded to batchCount, retries per the ADF
/// policy on virtual time, and no retry for data errors. AT-01 to AT-04 run the same orchestrator against
/// real containers.
/// </summary>
public sealed class ExtractOrchestratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);
    private static readonly PipelineRunInput Input = new(new DateOnly(2026, 11, 4), Guid.Parse("11111111-2222-3333-4444-555555555555"));

    private readonly FakeExtractReportingStore store = new();
    private readonly FakeStagingCopier copier = new();
    private readonly VirtualClock clock = new(Start);
    private readonly InProcessFunctionHost host;

    public ExtractOrchestratorTests()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IExtractReportingStore>(store)
            .AddSingleton<IKeyStoneStagingCopier>(copier)
            .AddSingleton(ExtractActivityPolicies.Adf)
            .AddSingleton<ExtractKeyStoneSteps>()
            .BuildServiceProvider();
        host = new InProcessFunctionHost(FunctionCatalog.FromAssembly(typeof(ExtractKeyStoneActivities).Assembly), services, clock);
        store.Tables = ExtractTestData.Tables(3);
    }

    [Fact]
    public async Task The_activities_run_in_the_ADF_order_and_log_the_counts_and_notes_the_golden_files_expect()
    {
        var result = await host.RunOrchestrationAsync(PipelineNames.Extract, Input);

        result.Succeeded.ShouldBeTrue(result.Describe());
        var calls = store.Calls;
        calls[0].ShouldBe("log Started");
        calls[1].ShouldBe("lookup");
        calls.Skip(2).Take(3).Order(StringComparer.Ordinal)
            .ShouldBe(["log Succeeded CopyToStaging stg.KsT1", "log Succeeded CopyToStaging stg.KsT2", "log Succeeded CopyToStaging stg.KsT3"]);
        calls[5].ShouldBe($"merge 2026-11-04 {Input.BatchRunId}");
        calls[6].ShouldBe("log Succeeded");
        calls.Count.ShouldBe(7);

        var events = store.Events;
        events.ShouldAllBe(e => e.BatchRunId == Input.BatchRunId && e.BusinessDate == Input.BusinessDate && e.PipelineName == PipelineNames.Extract);
        events[0].StartedUtc.ShouldBe(Start.UtcDateTime, "the orchestrator's clock, not DateTime.UtcNow");
        events.Where(e => e.StepName == "CopyToStaging").ShouldAllBe(e => e.RecordCount == 10);
        var succeeded = events[^1];
        succeeded.RecordCount.ShouldBe(3, "the number of tables extracted");
        succeeded.Notes.ShouldBe("Tables extracted (see CopyToStaging and MergeKeyStone events for row counts).");
        succeeded.EndedUtc.ShouldBe(Start.UtcDateTime);
    }

    [Fact]
    public async Task At_most_three_tables_are_copied_at_once()
    {
        store.Tables = ExtractTestData.Tables(7);
        copier.OnCopy = (_, _, token) => Task.Delay(40, token);

        var result = await host.RunOrchestrationAsync(PipelineNames.Extract, Input);

        result.Succeeded.ShouldBeTrue(result.Describe());
        copier.MaxConcurrent.ShouldBe(ExtractActivityPolicies.BatchCount);
        store.Events.Count(e => e.StepName == "CopyToStaging").ShouldBe(7);
    }

    [Fact]
    public async Task A_transient_copy_failure_is_retried_per_the_ADF_policy_on_virtual_time()
    {
        copier.OnCopy = (table, attempt, _) => table.TargetName == "stg.KsT2" && attempt <= 2 ? throw new TimeoutException("db2 down") : Task.CompletedTask;
        var policy = AdfActivityPolicy.Read(PipelineNames.Extract, "Copy Db2 To Staging");

        var result = await host.RunOrchestrationAsync(PipelineNames.Extract, Input);

        result.Succeeded.ShouldBeTrue(result.Describe());
        copier.AttemptsFor("stg.KsT2").ShouldBe(3);
        var failed = host.Trace.FailedAttempts;
        failed.Count.ShouldBe(2);
        failed.ShouldAllBe(a => policy.IsImplementedBy(a.Policy));
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).Select(d => d.Delay).ShouldAllBe(d => d == policy.RetryInterval);
        (clock.GetUtcNow() - Start).ShouldBe(TimeSpan.FromSeconds(60), "two 30-second intervals, none waited in real time");
    }

    [Fact]
    public async Task A_copy_that_keeps_failing_stops_after_retry_plus_one_attempts_and_nothing_is_merged()
    {
        copier.OnCopy = (table, _, _) => table.TargetName == "stg.KsT1" ? throw new TimeoutException("db2 down") : Task.CompletedTask;

        var result = await host.RunOrchestrationAsync(PipelineNames.Extract, Input);

        result.Succeeded.ShouldBeFalse();
        copier.AttemptsFor("stg.KsT1").ShouldBe(AdfActivityPolicy.Read(PipelineNames.Extract, "Copy Db2 To Staging").MaxNumberOfAttempts);
        store.Calls.Any(c => c.StartsWith("merge", StringComparison.Ordinal)).ShouldBeFalse();
        store.Events.Any(e => e.Status == "Succeeded" && e.StepName == null).ShouldBeFalse("Log Extract Succeeded runs only after the merge");
    }

    [Fact]
    public async Task A_data_error_is_not_retried()
    {
        copier.OnCopy = (table, _, _) => table.TargetName == "stg.KsT2" ? throw new ExtractDataException("stg.KsT2 row 3 (key A=****0003) column B: a CHAR value contains non-ASCII characters") : Task.CompletedTask;

        var result = await host.RunOrchestrationAsync(PipelineNames.Extract, Input);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(ExtractDataException).FullName);
        result.Failure.InnerFailure.ErrorMessage.ShouldContain("****0003");
        copier.AttemptsFor("stg.KsT2").ShouldBe(1);
        host.Trace.ShouldNotHaveRetriedAnything();
        store.Calls.Any(c => c.StartsWith("merge", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_log_event_is_retried_per_the_ADF_log_policy()
    {
        var logAttempts = 0;
        store.OnLog = (request, _) => request.Status == "Started" && Interlocked.Increment(ref logAttempts) == 1 ? throw new TimeoutException("sql down") : Task.CompletedTask;

        var result = await host.RunOrchestrationAsync(PipelineNames.Extract, Input);

        result.Succeeded.ShouldBeTrue(result.Describe());
        logAttempts.ShouldBe(2);
        AdfActivityPolicy.Read(PipelineNames.Extract, "Log Extract Started").IsImplementedBy(host.Trace.FailedAttempts.Single().Policy).ShouldBeTrue();
    }
}

internal static class OrchestrationTraceExtensions
{
    public static void ShouldNotHaveRetriedAnything(this OrchestrationTrace trace) =>
        trace.RetryDecisions.Where(d => d.Delay is not null).ShouldBeEmpty();
}
