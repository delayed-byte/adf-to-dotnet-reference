namespace CreditUnion.Batch.Core.Extract;

/// <summary>
/// Input of the Log activities: one call of <c>ops.usp_LogBatchRunEvent</c>. Empty strings are not used;
/// unset optional values are null. Timestamps are UTC and are taken from the orchestrator's clock
/// (ADF: <c>utcNow()</c>).
/// </summary>
public sealed record LogBatchRunEventRequest(
    Guid BatchRunId,
    DateOnly BusinessDate,
    string PipelineName,
    string Status,
    string? StepName = null,
    string? ObjectName = null,
    long? RecordCount = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? Notes = null,
    DateTime? StartedUtc = null,
    DateTime? EndedUtc = null);

/// <summary>Input of the Copy Db2 To Staging activity: one table for one business date.</summary>
public sealed record CopyTableRequest(ExtractTableConfig Table, DateOnly BusinessDate);

/// <summary>The reporting store operations the extract needs (LS_Reporting_Sql, login cu_batch_writer).</summary>
public interface IExtractReportingStore
{
    /// <summary>Appends one event to <c>ops.BatchRun</c> through <c>ops.usp_LogBatchRunEvent</c>.</summary>
    Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken);

    /// <summary>The enabled rows of <c>ops.ExtractConfig</c> in <c>SortOrder</c>.</summary>
    Task<IReadOnlyList<ExtractTableConfig>> LookupExtractConfigAsync(CancellationToken cancellationToken);

    /// <summary><c>core.usp_MergeKeyStone</c>: upserts staging into core and logs the merge counts.</summary>
    Task MergeKeyStoneAsync(DateOnly businessDate, Guid batchRunId, CancellationToken cancellationToken);
}

/// <summary>Copies one KeyStone table's rows for a business date into staging (the Copy activity).</summary>
public interface IKeyStoneStagingCopier
{
    /// <summary>Truncates the staging table, streams the Db2 rows into it and returns the number of rows copied.</summary>
    Task<long> CopyToStagingAsync(ExtractTableConfig table, DateOnly businessDate, CancellationToken cancellationToken);
}
