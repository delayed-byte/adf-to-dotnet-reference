using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.Core.Reconciliation;

/// <summary>
/// The work of the PL_Reconciliation activities (ADR 0013). The Functions activities are thin wrappers around these
/// methods, one per ADF activity that does I/O. Each method enforces its ADF <c>timeout</c> for one attempt and nothing
/// else: retries are the orchestrator's <c>RetryPolicy</c> (ADR 0008). Logs carry counts, amounts and the date; never
/// the webhook URL, the alert text, or a member, account or card number.
/// </summary>
public sealed partial class ReconciliationSteps(
    IReconciliationStore store,
    IReconAlertClient alerts,
    ReconActivityPolicies policies,
    ILogger<ReconciliationSteps> logger)
{
    /// <summary>Log Recon Started / Log Recon Outcome.</summary>
    public Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Timed("Log event", policies.LogEvent, async token =>
        {
            await store.LogBatchRunEventAsync(request, token);
            return 0;
        }, cancellationToken);
    }

    /// <summary>Lookup Card Totals: <c>ops.usp_GetCardReconTotals</c>'s first row.</summary>
    public async Task<CardReconTotals> LookupCardTotalsAsync(ReconQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ReconFormat.ValidateTolerance(query.Tolerance);
        var totals = await Timed("Lookup Card Totals", policies.CardTotals, token => store.GetCardTotalsAsync(query, token), cancellationToken);
        LogCardTotals(query.BusinessDate, totals.SettlementTotal, totals.KeyStoneTotal, totals.Difference);
        return totals;
    }

    /// <summary>Lookup GL Totals: <c>ops.usp_GetGlReconTotals</c>'s first row.</summary>
    public async Task<GlReconTotals> LookupGlTotalsAsync(ReconQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ReconFormat.ValidateTolerance(query.Tolerance);
        var totals = await Timed("Lookup GL Totals", policies.GlTotals, token => store.GetGlTotalsAsync(query, token), cancellationToken);
        LogGlTotals(query.BusinessDate, totals.ItemsCompared, totals.ItemsOutOfTolerance);
        return totals;
    }

    /// <summary>Record Recon Exceptions: a Lookup over a procedure that writes (ADR 0004). Safe to repeat: a known exception adds no row.</summary>
    public async Task<ReconExceptionResult> RecordExceptionsAsync(RecordReconExceptionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReconFormat.ValidateTolerance(request.Tolerance);
        var result = await Timed("Record Recon Exceptions", policies.RecordExceptions, token => store.RecordExceptionsAsync(request, token), cancellationToken);
        LogRecorded(request.BusinessDate, result.ExceptionCount, result.NewExceptionCount, result.AlertRequired);
        return result;
    }

    /// <summary>
    /// Post Recon Alert: one POST per attempt, bounded by the activity timeout. The URL is resolved by the client from
    /// configuration. At-least-once delivery at best (ADR 0013).
    /// </summary>
    public async Task PostAlertAsync(ReconAlert alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var body = ReconFormat.AlertBody(alert);
        await Timed("Post Recon Alert", policies.PostAlert, async token =>
        {
            await alerts.PostAsync(body, token);
            return 0;
        }, cancellationToken);
        LogAlertPosted(alert.BusinessDate, alert.ExceptionCount);
    }

    /// <summary>Mark Recon Alert Sent: <c>ops.usp_MarkReconAlertSent</c>, which stamps the date's unalerted exceptions.</summary>
    public async Task MarkAlertSentAsync(MarkReconAlertSentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Timed("Mark Recon Alert Sent", policies.MarkAlertSent, async token =>
        {
            await store.MarkAlertSentAsync(request, token);
            return 0;
        }, cancellationToken);
        LogMarked(request.BusinessDate);
    }

    private static Task<T> Timed<T>(string activity, ActivityPolicy policy, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        AttemptTimeout.RunAsync(activity, policy.Timeout, work, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Card reconciliation for {BusinessDate}: settlement total {SettlementTotal:F2}, KeyStone total {KeyStoneTotal:F2}, difference {Difference:F2}.")]
    private partial void LogCardTotals(DateOnly businessDate, decimal settlementTotal, decimal keyStoneTotal, decimal difference);

    [LoggerMessage(Level = LogLevel.Information, Message = "GL reconciliation for {BusinessDate}: {ItemsCompared} item(s) compared, {ItemsOutOfTolerance} out of tolerance.")]
    private partial void LogGlTotals(DateOnly businessDate, int itemsCompared, int itemsOutOfTolerance);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation exceptions for {BusinessDate}: {ExceptionCount} current, {NewExceptionCount} new, alert required: {AlertRequired}.")]
    private partial void LogRecorded(DateOnly businessDate, int exceptionCount, int newExceptionCount, bool alertRequired);

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted the reconciliation alert for {BusinessDate} ({ExceptionCount} exception(s)).")]
    private partial void LogAlertPosted(DateOnly businessDate, int exceptionCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked the reconciliation exceptions for {BusinessDate} as alerted.")]
    private partial void LogMarked(DateOnly businessDate);
}
