using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Map;

/// <summary>
/// The output of the Web activity "Get GL Mapping From KeyBridge" that the pipeline uses:
/// <c>output.version</c> and <c>output.mappings</c>, the latter kept as JSON text because the next
/// activity passes it on as <c>@string(...)</c> (ADR 0012). The mapping is reference data, not member data.
/// </summary>
public sealed record KeyBridgeGlMappings(string? Version, string MappingsJson);

/// <summary>Input of "Cache GL Mapping": the parameters of <c>ref.usp_UpsertTxnCodeGlMap</c>.</summary>
public sealed record CacheGlMappingRequest(Guid BatchRunId, string MappingsJson, string? SourceVersion);

/// <summary>The first row of <c>ref.usp_GetUnmappedTxnCodes</c> (ADF: the Lookup's <c>firstRow</c>).</summary>
public sealed record UnmappedTxnCodes(int UnmappedCount, string UnmappedCodes)
{
    /// <summary>
    /// The text of the ADF expression <c>@concat('Unmapped transaction codes: ', ...firstRow.UnmappedCodes)</c>,
    /// used by both "Log Unmapped Txn Codes" and the Fail activity.
    /// </summary>
    public string FailureMessage => "Unmapped transaction codes: " + UnmappedCodes;
}

/// <summary>One row of <c>ref.TxnCodeGlMap</c> as the data flow's <c>GlMap</c> source reads it. CHAR values keep their padding.</summary>
public sealed record GlMapping(string TxnCode, string GlAccountNbr, string DrCr, decimal AllocationRatio)
{
    public const string Debit = "D";
    public const string Credit = "C";
}

/// <summary>One row of the data flow's <c>Transactions</c> source (<c>core.PostedTransaction</c>). TxnId is the table's key.</summary>
public sealed record PostedAmount(long TxnId, string TxnCode, DateOnly PostingDate, decimal Amount);

/// <summary>One row of <c>core.DailyGlTotals</c>. NetAmount = DebitTotal - CreditTotal.</summary>
public sealed record DailyGlTotal(DateOnly BusinessDate, string GlAccountNbr, decimal DebitTotal, decimal CreditTotal, decimal NetAmount, int TxnCount);

/// <summary>The KeyBridge API (ADR 0012). The implementation makes exactly one HTTP request per call and never retries.</summary>
public interface IKeyBridgeClient
{
    /// <summary><c>GET {baseUrl}/v1/reference/transaction-codes/gl-mappings</c>, correlated by <paramref name="batchRunId"/>.</summary>
    Task<KeyBridgeGlMappings> GetGlMappingsAsync(Guid batchRunId, CancellationToken cancellationToken);
}

/// <summary>The reporting-store calls of PL_MapAndAggregate (login cu_batch_writer).</summary>
public interface IMapAndAggregateStore
{
    /// <summary><c>ops.usp_LogBatchRunEvent</c>.</summary>
    Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken);

    /// <summary><c>ref.usp_UpsertTxnCodeGlMap</c>. A mapping the procedure rejects raises <see cref="GlMappingInvalidException"/>.</summary>
    Task UpsertGlMappingAsync(CacheGlMappingRequest request, CancellationToken cancellationToken);

    /// <summary><c>ref.usp_GetUnmappedTxnCodes</c>.</summary>
    Task<UnmappedTxnCodes> GetUnmappedTxnCodesAsync(DateOnly businessDate, CancellationToken cancellationToken);

    /// <summary>The data flow's <c>GlMap</c> source: every row of <c>ref.TxnCodeGlMap</c>.</summary>
    Task<IReadOnlyList<GlMapping>> ReadGlMapAsync(CancellationToken cancellationToken);

    /// <summary>The data flow's <c>Transactions</c> source: <c>core.PostedTransaction</c> rows whose PostingDate is the business date, streamed.</summary>
    IAsyncEnumerable<PostedAmount> ReadPostedAmountsAsync(DateOnly businessDate, CancellationToken cancellationToken);

    /// <summary>The sink: deletes the date's <c>core.DailyGlTotals</c> (the pre-SQL) and inserts <paramref name="totals"/>, in one transaction.</summary>
    Task ReplaceDailyGlTotalsAsync(DateOnly businessDate, IReadOnlyList<DailyGlTotal> totals, CancellationToken cancellationToken);
}
