using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.Core.Map;

/// <summary>
/// The work of the PL_MapAndAggregate activities (ADR 0011, ADR 0012). The Functions activities are thin wrappers
/// around these methods, one per ADF activity that does I/O. Each method enforces its ADF <c>timeout</c> for one
/// attempt and nothing else: retries are the orchestrator's <c>RetryPolicy</c> (ADR 0008). Logs carry counts,
/// transaction codes, GL account numbers and the date; never a member, account or card number, a URL or a credential.
/// </summary>
public sealed partial class MapAndAggregateSteps(
    IMapAndAggregateStore store,
    IKeyBridgeClient keyBridge,
    MapActivityPolicies policies,
    ILogger<MapAndAggregateSteps> logger)
{
    /// <summary>Log Map Started / Log Map Succeeded.</summary>
    public Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Timed("Log event", policies.LogEvent, async token =>
        {
            await store.LogBatchRunEventAsync(request, token);
            return 0;
        }, cancellationToken);
    }

    /// <summary>Log Unmapped Txn Codes (the If Condition's true branch).</summary>
    public Task LogUnmappedTxnCodesAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Timed("Log Unmapped Txn Codes", policies.LogUnmapped, async token =>
        {
            await store.LogBatchRunEventAsync(request, token);
            return 0;
        }, cancellationToken);
    }

    /// <summary>Get GL Mapping From KeyBridge: one GET per attempt, bounded by the activity timeout.</summary>
    public async Task<KeyBridgeGlMappings> GetGlMappingAsync(PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var mappings = await Timed("Get GL Mapping From KeyBridge", policies.GetGlMapping,
            token => keyBridge.GetGlMappingsAsync(input.BatchRunId, token), cancellationToken);
        LogMappingReceived(mappings.Version ?? "(none)", mappings.MappingsJson.Length, input.BusinessDate);
        return mappings;
    }

    /// <summary>Cache GL Mapping: <c>ref.usp_UpsertTxnCodeGlMap</c> validates the whole set and replaces the cache.</summary>
    public async Task CacheGlMappingAsync(CacheGlMappingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Timed("Cache GL Mapping", policies.CacheGlMapping, async token =>
        {
            await store.UpsertGlMappingAsync(request, token);
            return 0;
        }, cancellationToken);
        LogMappingCached(request.SourceVersion ?? "(none)");
    }

    /// <summary>Lookup Unmapped Txn Codes: <c>ref.usp_GetUnmappedTxnCodes</c>'s first row.</summary>
    public async Task<UnmappedTxnCodes> LookupUnmappedTxnCodesAsync(PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await Timed("Lookup Unmapped Txn Codes", policies.LookupUnmapped,
            token => store.GetUnmappedTxnCodesAsync(input.BusinessDate, token), cancellationToken);
        LogUnmapped(result.UnmappedCount, result.UnmappedCodes, input.BusinessDate);
        return result;
    }

    /// <summary>
    /// Aggregate Daily GL Totals (DF_MapAndAggregateGl): reads the mapping and streams the date's transactions through
    /// <see cref="DailyGlTotalsAggregator"/>, then replaces the date's totals in one transaction. Returns the rows written.
    /// </summary>
    public async Task<int> AggregateDailyGlTotalsAsync(PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (rows, transactions) = await Timed("Aggregate Daily GL Totals", policies.Aggregate, async token =>
        {
            var map = await store.ReadGlMapAsync(token);
            var aggregator = new DailyGlTotalsAggregator(input.BusinessDate, map);
            await foreach (var transaction in store.ReadPostedAmountsAsync(input.BusinessDate, token).WithCancellation(token))
            {
                aggregator.Add(transaction);
            }

            var totals = aggregator.Complete();
            await store.ReplaceDailyGlTotalsAsync(input.BusinessDate, totals, token);
            return (totals.Count, aggregator.TransactionCount);
        }, cancellationToken);
        LogAggregated(transactions, rows, input.BusinessDate);
        return rows;
    }

    private static Task<T> Timed<T>(string activity, ActivityPolicy policy, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        AttemptTimeout.RunAsync(activity, policy.Timeout, work, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Received the KeyBridge GL mapping version {Version} ({Characters} characters of mappings) for {BusinessDate}.")]
    private partial void LogMappingReceived(string version, int characters, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cached the GL mapping version {Version} in ref.TxnCodeGlMap.")]
    private partial void LogMappingCached(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "{UnmappedCount} transaction code(s) posted on {BusinessDate} have no GL mapping: '{UnmappedCodes}'.")]
    private partial void LogUnmapped(int unmappedCount, string unmappedCodes, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aggregated {TransactionCount} transaction(s) posted on {BusinessDate} into {RowCount} core.DailyGlTotals row(s).")]
    private partial void LogAggregated(long transactionCount, int rowCount, DateOnly businessDate);
}
