using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.Functions.Activities;

/// <summary>
/// Activity names of PL_MapAndAggregate: one per ADF activity that does I/O ("Log Map Started" and "Log Map Succeeded"
/// share one). The If Condition and the Fail activity are deterministic orchestrator code (ADR 0012).
/// </summary>
public static class MapActivityNames
{
    public const string LogEvent = "Map_LogBatchRunEvent";
    public const string GetGlMapping = "Map_GetGlMappingFromKeyBridge";
    public const string CacheGlMapping = "Map_CacheGlMapping";
    public const string LookupUnmapped = "Map_LookupUnmappedTxnCodes";
    public const string LogUnmapped = "Map_LogUnmappedTxnCodes";
    public const string Aggregate = "Map_AggregateDailyGlTotals";
}

/// <summary>
/// The activities of PL_MapAndAggregate: thin wrappers over <see cref="MapAndAggregateSteps"/>, which holds the work.
/// Retries come from the orchestrator's RetryPolicy, never from inside an activity.
/// </summary>
public sealed class MapAndAggregateActivities(MapAndAggregateSteps steps)
{
    [Function(MapActivityNames.LogEvent)]
    public Task LogEventAsync([ActivityTrigger] LogBatchRunEventRequest request, CancellationToken cancellationToken) =>
        steps.LogEventAsync(request, cancellationToken);

    [Function(MapActivityNames.GetGlMapping)]
    public Task<KeyBridgeGlMappings> GetGlMappingAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.GetGlMappingAsync(input, cancellationToken);

    [Function(MapActivityNames.CacheGlMapping)]
    public Task CacheGlMappingAsync([ActivityTrigger] CacheGlMappingRequest request, CancellationToken cancellationToken) =>
        steps.CacheGlMappingAsync(request, cancellationToken);

    [Function(MapActivityNames.LookupUnmapped)]
    public Task<UnmappedTxnCodes> LookupUnmappedAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.LookupUnmappedTxnCodesAsync(input, cancellationToken);

    [Function(MapActivityNames.LogUnmapped)]
    public Task LogUnmappedAsync([ActivityTrigger] LogBatchRunEventRequest request, CancellationToken cancellationToken) =>
        steps.LogUnmappedTxnCodesAsync(request, cancellationToken);

    [Function(MapActivityNames.Aggregate)]
    public Task<int> AggregateAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.AggregateDailyGlTotalsAsync(input, cancellationToken);
}
