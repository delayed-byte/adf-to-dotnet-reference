using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.Core.Master;

/// <summary>
/// The work of the PL_Master_NightlyBatch activities (ADR 0014, ADR 0015). The Functions activities are thin wrappers around
/// these methods. Each method enforces its ADF <c>timeout</c> for one attempt and nothing else: retries are the orchestrator's
/// <c>RetryPolicy</c> (ADR 0008). Logs carry the date, counts and the evidence path; never a connection string, a member,
/// account or card number, or an evidence value.
/// </summary>
public sealed partial class NightlyBatchSteps(
    INightlyBatchStore store,
    IEvidenceBlobStore blobs,
    NightlyBatchSettings settings,
    MasterActivityPolicies policies,
    ILogger<NightlyBatchSteps> logger)
{
    /// <summary>Log Batch Started, Log Batch Succeeded and the five "Log Failed At …" activities.</summary>
    public Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AttemptTimeout.RunAsync("Log event", policies.LogEvent.Timeout, async token =>
        {
            await store.LogBatchRunEventAsync(request, token);
            return 0;
        }, cancellationToken);
    }

    /// <summary>
    /// Export Run Evidence (a Copy: <c>ops.usp_GetRunEvidence</c> to a JSON blob). The rows stream from SQL into the blob with
    /// no intermediate file, and the blob is committed only when the whole result set has been written, so a failed attempt
    /// leaves nothing behind. The path holds the batch run ID: a new run always writes a new file, and only a retry of the same
    /// run's export can meet an existing one, which it replaces (ADR 0015).
    /// </summary>
    public async Task<RunEvidenceExport> ExportRunEvidenceAsync(ExportRunEvidenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metadata = RunEvidenceFile.Metadata(request.RetainUntil);
        var blobName = RunEvidenceFile.BlobName(request.BusinessDate, request.BatchRunId);
        var entries = await AttemptTimeout.RunAsync("Export Run Evidence", policies.ExportEvidence.Timeout, async token =>
        {
            var count = 0;
            await blobs.WriteAsync(settings.EvidenceContainer, blobName, metadata, RunEvidenceFile.ContentType,
                async (stream, ct) => count = await RunEvidenceFile.WriteJsonAsync(store.ReadRunEvidenceAsync(request.BatchRunId, request.BusinessDate, ct), stream, ct),
                token);
            return count;
        }, cancellationToken);

        var objectName = RunEvidenceFile.ObjectName(settings.EvidenceContainer, request.BusinessDate, request.BatchRunId);
        LogEvidenceExported(request.BusinessDate, objectName, entries);
        return new RunEvidenceExport(objectName, entries);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Exported the run evidence for {BusinessDate:yyyy-MM-dd} to {ObjectName} ({EntryCount} entries).")]
    private partial void LogEvidenceExported(DateOnly businessDate, string objectName, int entryCount);
}
