using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.Core.Extract;

/// <summary>
/// The work of the PL_ExtractKeyStone activities. The Functions activities are thin wrappers around
/// these methods. Each method enforces the ADF activity <c>timeout</c> for one attempt and nothing
/// else: retries are the orchestrator's <c>RetryPolicy</c> (ADR 0008), so there is no retry loop here.
/// </summary>
public sealed partial class ExtractKeyStoneSteps(
    IExtractReportingStore reporting,
    IKeyStoneStagingCopier copier,
    ExtractActivityPolicies policies,
    ILogger<ExtractKeyStoneSteps> logger)
{
    public Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WithTimeoutAsync("Log event", policies.LogEvent.Timeout, async token =>
        {
            await reporting.LogEventAsync(request, token);
            return 0;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractTableConfig>> LookupExtractConfigAsync(Pipelines.PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tables = await WithTimeoutAsync("Lookup Extract Config", policies.LookupConfig.Timeout, reporting.LookupExtractConfigAsync, cancellationToken);
        LogConfig(input.BusinessDate, tables.Count);
        return tables;
    }

    public async Task<long> CopyToStagingAsync(CopyTableRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rows = await WithTimeoutAsync("Copy Db2 To Staging", policies.CopyToStaging.Timeout,
            token => copier.CopyToStagingAsync(request.Table, request.BusinessDate, token), cancellationToken);
        LogCopied(rows, request.Table.SourceObject, request.Table.TargetName, request.BusinessDate);
        return rows;
    }

    public async Task MergeKeyStoneAsync(Pipelines.PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await WithTimeoutAsync("Merge KeyStone To Core", policies.Merge.Timeout, async token =>
        {
            await reporting.MergeKeyStoneAsync(input.BusinessDate, input.BatchRunId, token);
            return 0;
        }, cancellationToken);
        LogMerged(input.BusinessDate);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Extract config for {BusinessDate}: {TableCount} enabled table(s).")]
    private partial void LogConfig(DateOnly businessDate, int tableCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {RowCount} row(s) from {Source} to {Target} for {BusinessDate}.")]
    private partial void LogCopied(long rowCount, string source, string target, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merged KeyStone staging into core for {BusinessDate}.")]
    private partial void LogMerged(DateOnly businessDate);

    private static Task<T> WithTimeoutAsync<T>(string activity, TimeSpan timeout, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        Pipelines.AttemptTimeout.RunAsync(activity, timeout, work, cancellationToken);
}
