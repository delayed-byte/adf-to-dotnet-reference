using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.Functions.Activities;

/// <summary>Activity names of PL_ExtractKeyStone (ADR 0008 names the orchestrator; these are its activities).</summary>
public static class ExtractActivityNames
{
    public const string LogEvent = "Extract_LogBatchRunEvent";
    public const string LookupConfig = "Extract_LookupExtractConfig";
    public const string CopyToStaging = "Extract_CopyDb2ToStaging";
    public const string Merge = "Extract_MergeKeyStoneToCore";
}

/// <summary>
/// The activities of PL_ExtractKeyStone: thin wrappers over <see cref="ExtractKeyStoneSteps"/>, which holds
/// the work. Retries come from the orchestrator's RetryPolicy, never from inside an activity.
/// </summary>
public sealed class ExtractKeyStoneActivities(ExtractKeyStoneSteps steps)
{
    [Function(ExtractActivityNames.LogEvent)]
    public Task LogEventAsync([ActivityTrigger] LogBatchRunEventRequest request, CancellationToken cancellationToken) =>
        steps.LogEventAsync(request, cancellationToken);

    [Function(ExtractActivityNames.LookupConfig)]
    public Task<IReadOnlyList<ExtractTableConfig>> LookupExtractConfigAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.LookupExtractConfigAsync(input, cancellationToken);

    [Function(ExtractActivityNames.CopyToStaging)]
    public Task<long> CopyToStagingAsync([ActivityTrigger] CopyTableRequest request, CancellationToken cancellationToken) =>
        steps.CopyToStagingAsync(request, cancellationToken);

    [Function(ExtractActivityNames.Merge)]
    public Task MergeKeyStoneAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.MergeKeyStoneAsync(input, cancellationToken);
}
