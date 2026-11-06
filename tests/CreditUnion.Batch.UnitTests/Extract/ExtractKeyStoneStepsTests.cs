using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreditUnion.Batch.UnitTests.Extract;

public sealed class ExtractKeyStoneStepsTests
{
    private static readonly PipelineRunInput Input = new(new DateOnly(2026, 11, 4), Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private readonly FakeExtractReportingStore store = new() { Tables = ExtractTestData.Tables(2) };
    private readonly FakeStagingCopier copier = new();

    private ExtractKeyStoneSteps Steps(ExtractActivityPolicies? policies = null) =>
        new(store, copier, policies ?? ExtractActivityPolicies.Adf, NullLogger<ExtractKeyStoneSteps>.Instance);

    [Fact]
    public async Task Each_step_delegates_to_its_service_with_the_business_date_and_batch_run()
    {
        var steps = Steps();

        (await steps.LookupExtractConfigAsync(Input, CancellationToken.None)).Count.ShouldBe(2);
        (await steps.CopyToStagingAsync(new CopyTableRequest(ExtractTestData.Table("Alpha"), Input.BusinessDate), CancellationToken.None)).ShouldBe(10);
        await steps.MergeKeyStoneAsync(Input, CancellationToken.None);
        await steps.LogEventAsync(new LogBatchRunEventRequest(Input.BatchRunId, Input.BusinessDate, PipelineNames.Extract, "Started"), CancellationToken.None);

        store.Calls.ShouldBe(["lookup", $"merge 2026-11-04 {Input.BatchRunId}", "log Started"]);
        copier.AttemptsFor("stg.KsAlpha").ShouldBe(1);
    }

    [Fact]
    public async Task A_step_that_outlasts_its_ADF_timeout_fails_with_a_TimeoutException_for_the_retry_policy_to_handle()
    {
        copier.OnCopy = (_, _, token) => Task.Delay(Timeout.Infinite, token);
        var policies = ExtractActivityPolicies.Adf with { CopyToStaging = ExtractActivityPolicies.Adf.CopyToStaging with { Timeout = TimeSpan.FromMilliseconds(50) } };

        var error = await Should.ThrowAsync<TimeoutException>(() =>
            Steps(policies).CopyToStagingAsync(new CopyTableRequest(ExtractTestData.Table("Alpha"), Input.BusinessDate), CancellationToken.None));

        error.Message.ShouldContain("Copy Db2 To Staging");
        error.Message.ShouldContain("timeout");
    }

    [Fact]
    public async Task Cancelling_the_caller_is_a_cancellation_not_a_timeout()
    {
        store.OnMerge = token => Task.Delay(Timeout.Infinite, token);
        using var cancel = new CancellationTokenSource();

        var merge = Steps().MergeKeyStoneAsync(Input, cancel.Token);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => merge);
    }

    [Fact]
    public async Task A_data_error_from_the_copier_reaches_the_caller_unchanged_so_the_retry_policy_can_skip_it()
    {
        copier.OnCopy = (_, _, _) => throw new ExtractDataException("stg.KsAlpha row 7 (key A=****0007) column B: a CHAR value contains non-ASCII characters");

        var error = await Should.ThrowAsync<ExtractDataException>(() =>
            Steps().CopyToStagingAsync(new CopyTableRequest(ExtractTestData.Table("Alpha"), Input.BusinessDate), CancellationToken.None));

        error.Message.ShouldContain("****0007");
        copier.AttemptsFor("stg.KsAlpha").ShouldBe(1, "the steps never retry; the orchestrator's RetryPolicy does");
    }
}
