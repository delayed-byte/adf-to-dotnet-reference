using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Reconciliation;
using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.Functions.Activities;

/// <summary>
/// Activity names of PL_Reconciliation: one per ADF activity that does I/O ("Log Recon Started" and "Log Recon Outcome"
/// share one). The If Condition is deterministic orchestrator code, and "Get Alert Webhook Url" is configuration read inside
/// <see cref="PostAlert"/>, so the secret URL is never an activity input or output (ADR 0013).
/// </summary>
public static class ReconActivityNames
{
    public const string LogEvent = "Recon_LogBatchRunEvent";
    public const string LookupCardTotals = "Recon_LookupCardTotals";
    public const string LookupGlTotals = "Recon_LookupGlTotals";
    public const string RecordExceptions = "Recon_RecordReconExceptions";
    public const string PostAlert = "Recon_PostReconAlert";
    public const string MarkAlertSent = "Recon_MarkReconAlertSent";
}

/// <summary>
/// The activities of PL_Reconciliation: thin wrappers over <see cref="ReconciliationSteps"/>, which holds the work.
/// Retries come from the orchestrator's RetryPolicy, never from inside an activity.
/// </summary>
public sealed class ReconciliationActivities(ReconciliationSteps steps)
{
    [Function(ReconActivityNames.LogEvent)]
    public Task LogEventAsync([ActivityTrigger] LogBatchRunEventRequest request, CancellationToken cancellationToken) =>
        steps.LogEventAsync(request, cancellationToken);

    [Function(ReconActivityNames.LookupCardTotals)]
    public Task<CardReconTotals> LookupCardTotalsAsync([ActivityTrigger] ReconQuery query, CancellationToken cancellationToken) =>
        steps.LookupCardTotalsAsync(query, cancellationToken);

    [Function(ReconActivityNames.LookupGlTotals)]
    public Task<GlReconTotals> LookupGlTotalsAsync([ActivityTrigger] ReconQuery query, CancellationToken cancellationToken) =>
        steps.LookupGlTotalsAsync(query, cancellationToken);

    [Function(ReconActivityNames.RecordExceptions)]
    public Task<ReconExceptionResult> RecordExceptionsAsync([ActivityTrigger] RecordReconExceptionsRequest request, CancellationToken cancellationToken) =>
        steps.RecordExceptionsAsync(request, cancellationToken);

    [Function(ReconActivityNames.PostAlert)]
    public Task PostAlertAsync([ActivityTrigger] ReconAlert alert, CancellationToken cancellationToken) =>
        steps.PostAlertAsync(alert, cancellationToken);

    [Function(ReconActivityNames.MarkAlertSent)]
    public Task MarkAlertSentAsync([ActivityTrigger] MarkReconAlertSentRequest request, CancellationToken cancellationToken) =>
        steps.MarkAlertSentAsync(request, cancellationToken);
}
