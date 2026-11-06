using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.Core.Card;

/// <summary>
/// The work of the PL_IngestCardSettlement activities (ADR 0010). The Functions activities are thin wrappers
/// around these methods, one per ADF activity. Each method enforces its ADF <c>timeout</c> for one attempt and
/// nothing else: retries are the orchestrator's <c>RetryPolicy</c> (ADR 0008), so no retry loop is here, and the
/// stores switch the SDK retries off. Logs carry file names, counts and the date; never row values.
/// </summary>
public sealed partial class CardSettlementSteps(
    ICardSettlementStore store,
    ICardBlobStore blobs,
    CardSettlementSettings settings,
    CardActivityPolicies policies,
    ILogger<CardSettlementSteps> logger)
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Log Card Ingest Started / Log Card Ingest Succeeded.</summary>
    public Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Timed("Log event", policies.LogEvent, token => store.LogBatchRunEventAsync(request, token), cancellationToken);
    }

    /// <summary>Reset Card Staging.</summary>
    public Task ResetStagingAsync(PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Timed("Reset Card Staging", policies.ResetStaging, token => store.ResetStagingAsync(input.BusinessDate, token), cancellationToken);
    }

    /// <summary>List Landing Files: the direct children of <c>card-landing/{businessDate}/</c>; none when the folder does not exist.</summary>
    public async Task<IReadOnlyList<LandingItem>> ListLandingFilesAsync(PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var items = await Timed("List Landing Files", policies.ListLandingFiles,
            token => blobs.ListChildItemsAsync(settings.LandingContainer, Folder(input.BusinessDate), token), cancellationToken);
        LogListed(items.Count, input.BusinessDate);
        return items;
    }

    /// <summary>Copy File To Raw Staging: streams the CSV from the landing blob into <c>stg.CardSettlementRaw</c>; no intermediate file.</summary>
    public async Task<long> CopyFileToRawStagingAsync(SettlementFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rows = await Timed("Copy File To Raw Staging", policies.CopyToRawStaging, async token =>
        {
            await using var stream = await blobs.OpenReadAsync(settings.LandingContainer, BlobPath(request), token);
            using var reader = SettlementCsvReader.OpenText(stream);
            return await store.LoadRawAsync(request.BusinessDate, request.FileName, SettlementCsvReader.ReadAsync(reader, request.FileName, token), token);
        }, cancellationToken);
        LogLanded(request.FileName, rows, request.BusinessDate);
        return rows;
    }

    /// <summary>Validate Settlement File: header, row schema and trailer are checked by <c>stg.usp_ValidateCardSettlementFile</c>, not here.</summary>
    public async Task<SettlementValidation> ValidateFileAsync(SettlementFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Timed("Validate Settlement File", policies.Validate,
            token => store.ValidateFileAsync(request.BusinessDate, request.FileName, request.BatchRunId, token), cancellationToken);
        LogValidated(request.FileName, result.IsValid, result.ErrorCount, request.BusinessDate);
        return result;
    }

    /// <summary>Promote Valid File.</summary>
    public Task PromoteFileAsync(SettlementFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Timed("Promote Valid File", policies.Promote, token => store.PromoteFileAsync(request.BusinessDate, request.FileName, token), cancellationToken);
    }

    /// <summary>Archive Settlement File: byte-for-byte copy to <c>card-archive</c> with the <c>retain_until</c> and <c>batch_run_id</c> metadata.</summary>
    public Task ArchiveFileAsync(SettlementFileRequest request, CancellationToken cancellationToken) =>
        CopyAsync("Archive Settlement File", policies.Archive, settings.ArchiveContainer, request, cancellationToken);

    /// <summary>Quarantine Settlement File: the same copy into <c>card-quarantine</c>.</summary>
    public Task QuarantineFileAsync(SettlementFileRequest request, CancellationToken cancellationToken) =>
        CopyAsync("Quarantine Settlement File", policies.Quarantine, settings.QuarantineContainer, request, cancellationToken);

    /// <summary>Delete Archived Landing File.</summary>
    public Task DeleteArchivedLandingFileAsync(SettlementFileRequest request, CancellationToken cancellationToken) =>
        DeleteLandingAsync("Delete Archived Landing File", policies.DeleteArchived, request, cancellationToken);

    /// <summary>Delete Quarantined Landing File.</summary>
    public Task DeleteQuarantinedLandingFileAsync(SettlementFileRequest request, CancellationToken cancellationToken) =>
        DeleteLandingAsync("Delete Quarantined Landing File", policies.DeleteQuarantined, request, cancellationToken);

    /// <summary>
    /// Write Error Manifest: <c>stg.usp_GetCardSettlementValidationErrors</c> as a JSON array of objects in
    /// <c>card-quarantine/{businessDate}/{file}.errors.json</c>. The rows are the SQL procedure's, passed through.
    /// </summary>
    public Task WriteErrorManifestAsync(SettlementFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Timed("Write Error Manifest", policies.WriteManifest, async token =>
        {
            var rows = await store.GetValidationErrorsAsync(request.BusinessDate, request.FileName, token);
            var json = JsonSerializer.SerializeToUtf8Bytes(rows, ManifestJson);
            await blobs.WriteAsync(settings.QuarantineContainer, BlobPath(request) + ".errors.json", json, "application/json", token);
            LogManifest(request.FileName, rows.Count, request.BusinessDate);
        }, cancellationToken);
    }

    /// <summary>Log File Archived: counts and amounts from the Validate lookup, destination <c>card-archive/{date}/{file}</c>.</summary>
    public Task LogFileArchivedAsync(LogCardFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var v = request.Validation;
        return Timed("Log File Archived", policies.LogArchived, token => store.LogCardFileAsync(new LogCardFileCall(
            request.File.BatchRunId, request.File.BusinessDate, request.File.FileName, "Archived",
            v.DetailRowCount, v.TrailerRowCount, v.DetailTotalAmount, v.TrailerTotalAmount, null,
            Destination(settings.ArchiveContainer, request.File)), token), cancellationToken);
    }

    /// <summary>Log File Quarantined: counts and the error summary (no amounts), destination <c>card-quarantine/{date}/{file}</c>.</summary>
    public Task LogFileQuarantinedAsync(LogCardFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var v = request.Validation;
        return Timed("Log File Quarantined", policies.LogQuarantined, token => store.LogCardFileAsync(new LogCardFileCall(
            request.File.BatchRunId, request.File.BusinessDate, request.File.FileName, "Quarantined",
            v.DetailRowCount, v.TrailerRowCount, null, null, v.ErrorSummary,
            Destination(settings.QuarantineContainer, request.File)), token), cancellationToken);
    }

    /// <summary>Merge Card Settlement To Core.</summary>
    public async Task MergeToCoreAsync(PipelineRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await Timed("Merge Card Settlement To Core", policies.Merge, token => store.MergeAsync(input.BusinessDate, input.BatchRunId, token), cancellationToken);
        LogMerged(input.BusinessDate);
    }

    private static string Folder(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string BlobPath(SettlementFileRequest request) => Folder(request.BusinessDate) + "/" + request.FileName;

    private static string Destination(string container, SettlementFileRequest request) => container + "/" + BlobPath(request);

    private Task CopyAsync(string activity, ActivityPolicy policy, string destinationContainer, SettlementFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["retain_until"] = settings.RetainUntil,
            ["batch_run_id"] = request.BatchRunId.ToString("D", CultureInfo.InvariantCulture),
        };
        return Timed(activity, policy, async token =>
        {
            await blobs.CopyAsync(settings.LandingContainer, BlobPath(request), destinationContainer, BlobPath(request), metadata, token);
            LogCopied(request.FileName, destinationContainer, request.BusinessDate);
        }, cancellationToken);
    }

    private Task DeleteLandingAsync(string activity, ActivityPolicy policy, SettlementFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Timed(activity, policy, token => blobs.DeleteAsync(settings.LandingContainer, BlobPath(request), token), cancellationToken);
    }

    private static async Task Timed(string activity, ActivityPolicy policy, Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        await Timed(activity, policy, async token =>
        {
            await work(token);
            return 0;
        }, cancellationToken);

    private static Task<T> Timed<T>(string activity, ActivityPolicy policy, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        AttemptTimeout.RunAsync(activity, policy.Timeout, work, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Landing folder for {BusinessDate} has {ItemCount} item(s).")]
    private partial void LogListed(int itemCount, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Landed {RowCount} record(s) of {SourceFile} for {BusinessDate}.")]
    private partial void LogLanded(string sourceFile, long rowCount, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Validated {SourceFile} for {BusinessDate}: valid={IsValid}, {ErrorCount} error(s).")]
    private partial void LogValidated(string sourceFile, bool isValid, int errorCount, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {SourceFile} for {BusinessDate} to {Container}.")]
    private partial void LogCopied(string sourceFile, string container, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote the error manifest of {SourceFile} for {BusinessDate}: {ErrorCount} entr(ies).")]
    private partial void LogManifest(string sourceFile, int errorCount, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merged card settlement staging into core for {BusinessDate}.")]
    private partial void LogMerged(DateOnly businessDate);
}
