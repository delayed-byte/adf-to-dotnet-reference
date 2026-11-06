using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Reconciliation;

/// <summary>Input of "Lookup Card Totals" and "Lookup GL Totals": the BusinessDate and Tolerance parameters of the procedures.</summary>
public sealed record ReconQuery(DateOnly BusinessDate, decimal Tolerance);

/// <summary>Input of "Record Recon Exceptions".</summary>
public sealed record RecordReconExceptionsRequest(DateOnly BusinessDate, Guid BatchRunId, decimal Tolerance);

/// <summary>Input of "Mark Recon Alert Sent".</summary>
public sealed record MarkReconAlertSentRequest(DateOnly BusinessDate, Guid BatchRunId);

/// <summary>The first row of <c>ops.usp_GetCardReconTotals</c> (ADF: the Lookup's <c>firstRow</c>). Difference = settlement minus KeyStone.</summary>
public sealed record CardReconTotals(decimal SettlementTotal, decimal KeyStoneTotal, decimal Difference, decimal AbsDifference, bool IsOutOfTolerance);

/// <summary>
/// The first row of <c>ops.usp_GetGlReconTotals</c>. <c>ItemsOutOfTolerance</c> is NULL in SQL when nothing was
/// compared (SUM over no rows); it is 0 here (ADR 0013).
/// </summary>
public sealed record GlReconTotals(int ItemsCompared, int ItemsOutOfTolerance, decimal TotalAbsDifference, bool IsOutOfTolerance);

/// <summary>The row <c>ops.usp_RecordReconExceptions</c> always returns.</summary>
public sealed record ReconExceptionResult(bool IsOutOfTolerance, int ExceptionCount, int NewExceptionCount, bool AlertRequired, string AlertSummary);

/// <summary>
/// Input of "Post Recon Alert": the fields the ADF body expression reads. It carries no webhook URL: the URL embeds a
/// secret token and is resolved inside the activity from configuration, so it never crosses a Durable boundary (ADR 0013).
/// </summary>
public sealed record ReconAlert(DateOnly BusinessDate, Guid BatchRunId, decimal CardDifference, int GlItemsOutOfTolerance, int ExceptionCount, string Summary);

/// <summary>The reporting-store calls of PL_Reconciliation (login cu_batch_writer).</summary>
public interface IReconciliationStore
{
    /// <summary><c>ops.usp_LogBatchRunEvent</c>.</summary>
    Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken);

    /// <summary><c>ops.usp_GetCardReconTotals</c>.</summary>
    Task<CardReconTotals> GetCardTotalsAsync(ReconQuery query, CancellationToken cancellationToken);

    /// <summary><c>ops.usp_GetGlReconTotals</c>.</summary>
    Task<GlReconTotals> GetGlTotalsAsync(ReconQuery query, CancellationToken cancellationToken);

    /// <summary><c>ops.usp_RecordReconExceptions</c>: inserts the new out-of-tolerance items and says whether an alert is still due.</summary>
    Task<ReconExceptionResult> RecordExceptionsAsync(RecordReconExceptionsRequest request, CancellationToken cancellationToken);

    /// <summary><c>ops.usp_MarkReconAlertSent</c>.</summary>
    Task MarkAlertSentAsync(MarkReconAlertSentRequest request, CancellationToken cancellationToken);
}

/// <summary>The alert webhook (ADR 0013). One call is one HTTP request; the implementation never retries.</summary>
public interface IReconAlertClient
{
    /// <summary>POSTs <paramref name="jsonBody"/> to the configured webhook URL. A response outside 2xx raises an exception that says whether retrying can help.</summary>
    Task PostAsync(string jsonBody, CancellationToken cancellationToken);
}
