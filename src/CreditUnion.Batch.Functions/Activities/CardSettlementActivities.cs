using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.Functions.Activities;

/// <summary>
/// Activity names of PL_IngestCardSettlement: one per ADF activity that does I/O (the two Log activities that call
/// <c>ops.usp_LogBatchRunEvent</c> share one). The Filter activity is a pure function the orchestrator calls (ADR 0010).
/// </summary>
public static class CardActivityNames
{
    public const string LogEvent = "Card_LogBatchRunEvent";
    public const string ResetStaging = "Card_ResetCardStaging";
    public const string ListLandingFiles = "Card_ListLandingFiles";
    public const string CopyToRawStaging = "Card_CopyFileToRawStaging";
    public const string Validate = "Card_ValidateSettlementFile";
    public const string Promote = "Card_PromoteValidFile";
    public const string Archive = "Card_ArchiveSettlementFile";
    public const string DeleteArchived = "Card_DeleteArchivedLandingFile";
    public const string LogArchived = "Card_LogFileArchived";
    public const string WriteManifest = "Card_WriteErrorManifest";
    public const string Quarantine = "Card_QuarantineSettlementFile";
    public const string DeleteQuarantined = "Card_DeleteQuarantinedLandingFile";
    public const string LogQuarantined = "Card_LogFileQuarantined";
    public const string Merge = "Card_MergeCardSettlementToCore";
}

/// <summary>
/// The activities of PL_IngestCardSettlement: thin wrappers over <see cref="CardSettlementSteps"/>, which holds
/// the work. Retries come from the orchestrator's RetryPolicy, never from inside an activity.
/// </summary>
public sealed class CardSettlementActivities(CardSettlementSteps steps)
{
    [Function(CardActivityNames.LogEvent)]
    public Task LogEventAsync([ActivityTrigger] LogBatchRunEventRequest request, CancellationToken cancellationToken) =>
        steps.LogEventAsync(request, cancellationToken);

    [Function(CardActivityNames.ResetStaging)]
    public Task ResetStagingAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.ResetStagingAsync(input, cancellationToken);

    [Function(CardActivityNames.ListLandingFiles)]
    public Task<IReadOnlyList<LandingItem>> ListLandingFilesAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.ListLandingFilesAsync(input, cancellationToken);

    [Function(CardActivityNames.CopyToRawStaging)]
    public Task<long> CopyFileToRawStagingAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.CopyFileToRawStagingAsync(request, cancellationToken);

    [Function(CardActivityNames.Validate)]
    public Task<SettlementValidation> ValidateAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.ValidateFileAsync(request, cancellationToken);

    [Function(CardActivityNames.Promote)]
    public Task PromoteAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.PromoteFileAsync(request, cancellationToken);

    [Function(CardActivityNames.Archive)]
    public Task ArchiveAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.ArchiveFileAsync(request, cancellationToken);

    [Function(CardActivityNames.DeleteArchived)]
    public Task DeleteArchivedAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.DeleteArchivedLandingFileAsync(request, cancellationToken);

    [Function(CardActivityNames.LogArchived)]
    public Task LogArchivedAsync([ActivityTrigger] LogCardFileRequest request, CancellationToken cancellationToken) =>
        steps.LogFileArchivedAsync(request, cancellationToken);

    [Function(CardActivityNames.WriteManifest)]
    public Task WriteManifestAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.WriteErrorManifestAsync(request, cancellationToken);

    [Function(CardActivityNames.Quarantine)]
    public Task QuarantineAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.QuarantineFileAsync(request, cancellationToken);

    [Function(CardActivityNames.DeleteQuarantined)]
    public Task DeleteQuarantinedAsync([ActivityTrigger] SettlementFileRequest request, CancellationToken cancellationToken) =>
        steps.DeleteQuarantinedLandingFileAsync(request, cancellationToken);

    [Function(CardActivityNames.LogQuarantined)]
    public Task LogQuarantinedAsync([ActivityTrigger] LogCardFileRequest request, CancellationToken cancellationToken) =>
        steps.LogFileQuarantinedAsync(request, cancellationToken);

    [Function(CardActivityNames.Merge)]
    public Task MergeAsync([ActivityTrigger] PipelineRunInput input, CancellationToken cancellationToken) =>
        steps.MergeToCoreAsync(input, cancellationToken);
}
