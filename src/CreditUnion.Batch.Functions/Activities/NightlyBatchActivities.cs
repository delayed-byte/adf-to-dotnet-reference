using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Master;
using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.Functions.Activities;

/// <summary>
/// Activity names of PL_Master_NightlyBatch: one per ADF activity that does I/O ("Log Batch Started", "Log Batch Succeeded" and
/// the five "Log Failed At …" share one). The Execute Pipeline activities are sub-orchestrations; Resolve Business Date and the
/// Fail activities are deterministic orchestrator code (ADR 0014).
/// </summary>
public static class MasterActivityNames
{
    public const string LogEvent = "Master_LogBatchRunEvent";
    public const string ExportEvidence = "Master_ExportRunEvidence";
}

/// <summary>
/// The activities of PL_Master_NightlyBatch: thin wrappers over <see cref="NightlyBatchSteps"/>, which holds the work.
/// Retries come from the orchestrator's RetryPolicy, never from inside an activity.
/// </summary>
public sealed class NightlyBatchActivities(NightlyBatchSteps steps)
{
    [Function(MasterActivityNames.LogEvent)]
    public Task LogEventAsync([ActivityTrigger] LogBatchRunEventRequest request, CancellationToken cancellationToken) =>
        steps.LogEventAsync(request, cancellationToken);

    [Function(MasterActivityNames.ExportEvidence)]
    public Task<RunEvidenceExport> ExportRunEvidenceAsync([ActivityTrigger] ExportRunEvidenceRequest request, CancellationToken cancellationToken) =>
        steps.ExportRunEvidenceAsync(request, cancellationToken);
}
