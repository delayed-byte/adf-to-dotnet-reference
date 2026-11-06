using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Card;

/// <summary>One child item of the landing folder, as ADF's Get Metadata reports it: <c>type</c> is "File" or "Folder".</summary>
public sealed record LandingItem(string Name, string Type)
{
    public const string FileType = "File";
    public const string FolderType = "Folder";
}

/// <summary>Input of every per-file activity: the file's name (ADF <c>item().name</c>) and the pipeline parameters it needs.</summary>
public sealed record SettlementFileRequest(DateOnly BusinessDate, Guid BatchRunId, string FileName);

/// <summary>
/// The first row <c>stg.usp_ValidateCardSettlementFile</c> returns (ADF: the Lookup's <c>firstRow</c>).
/// The rules themselves live in SQL; this only carries the result.
/// </summary>
public sealed record SettlementValidation(
    string SourceFile,
    bool IsValid,
    int? DetailRowCount,
    int? TrailerRowCount,
    decimal? DetailTotalAmount,
    decimal? TrailerTotalAmount,
    int ErrorCount,
    string? ErrorSummary);

/// <summary>Input of the Log File Archived / Log File Quarantined activities: the file and what the Validate Lookup returned for it.</summary>
public sealed record LogCardFileRequest(SettlementFileRequest File, SettlementValidation Validation);

/// <summary>One row of <c>stg.usp_GetCardSettlementValidationErrors</c>: one entry of the error manifest. Never holds a full card number (the file carries only BIN and last 4).</summary>
public sealed record CardValidationErrorRow(
    string BusinessDate,
    string SourceFile,
    string BatchRunId,
    string? RecordType,
    string? ProcessorRefNbr,
    string ErrorCode,
    string ErrorMessage);

/// <summary>Where the pipeline's blobs live and how they are tagged: PL_IngestCardSettlement's container and retainUntil parameters, taken from configuration (ADR 0008).</summary>
public sealed record CardSettlementSettings(string LandingContainer, string ArchiveContainer, string QuarantineContainer, string RetainUntil)
{
    public static CardSettlementSettings Defaults { get; } = new("card-landing", "card-archive", "card-quarantine", "TBD");
}

/// <summary>The parameters of one <c>ops.usp_LogCardFile</c> call, exactly as the ADF activity passes them (unset ones are null).</summary>
public sealed record LogCardFileCall(
    Guid BatchRunId,
    DateOnly BusinessDate,
    string SourceFile,
    string Outcome,
    int? DetailRowCount,
    int? TrailerRowCount,
    decimal? DetailTotalAmount,
    decimal? TrailerTotalAmount,
    string? ErrorSummary,
    string DestinationPath);

/// <summary>The reporting-store calls of PL_IngestCardSettlement (login cu_batch_writer).</summary>
public interface ICardSettlementStore
{
    /// <summary><c>ops.usp_LogBatchRunEvent</c>.</summary>
    Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken);

    /// <summary><c>stg.usp_ResetCardSettlementStaging</c>.</summary>
    Task ResetStagingAsync(DateOnly businessDate, CancellationToken cancellationToken);

    /// <summary>
    /// The Copy activity's sink: deletes the file's rows for the date, then bulk-copies <paramref name="records"/>
    /// (ten string columns each) into <c>stg.CardSettlementRaw</c>. Returns the rows written.
    /// </summary>
    Task<long> LoadRawAsync(DateOnly businessDate, string sourceFile, IAsyncEnumerable<string?[]> records, CancellationToken cancellationToken);

    /// <summary><c>stg.usp_ValidateCardSettlementFile</c>.</summary>
    Task<SettlementValidation> ValidateFileAsync(DateOnly businessDate, string sourceFile, Guid batchRunId, CancellationToken cancellationToken);

    /// <summary><c>stg.usp_PromoteCardSettlementFile</c>.</summary>
    Task PromoteFileAsync(DateOnly businessDate, string sourceFile, CancellationToken cancellationToken);

    /// <summary><c>stg.usp_GetCardSettlementValidationErrors</c>.</summary>
    Task<IReadOnlyList<CardValidationErrorRow>> GetValidationErrorsAsync(DateOnly businessDate, string sourceFile, CancellationToken cancellationToken);

    /// <summary><c>ops.usp_LogCardFile</c>.</summary>
    Task LogCardFileAsync(LogCardFileCall entry, CancellationToken cancellationToken);

    /// <summary><c>core.usp_MergeCardSettlement</c>.</summary>
    Task MergeAsync(DateOnly businessDate, Guid batchRunId, CancellationToken cancellationToken);
}

/// <summary>Blob storage operations of PL_IngestCardSettlement (LS_Blob_Storage). The Azure SDK's own retries are off (ADR 0010).</summary>
public interface ICardBlobStore
{
    /// <summary>Get Metadata on a folder: its direct children (not recursive). A folder that does not exist has none.</summary>
    Task<IReadOnlyList<LandingItem>> ListChildItemsAsync(string container, string folderPath, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string container, string path, CancellationToken cancellationToken);

    /// <summary>Binary copy: the bytes unchanged, with <paramref name="metadata"/> on the destination, replacing an existing blob.</summary>
    Task CopyAsync(string sourceContainer, string sourcePath, string destinationContainer, string destinationPath, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken);

    /// <summary>Delete activity: removes the blob; a blob that is already gone is not an error, so a retry after a lost reply succeeds.</summary>
    Task DeleteAsync(string container, string path, CancellationToken cancellationToken);

    /// <summary>Writes a blob from memory, replacing an existing one.</summary>
    Task WriteAsync(string container, string path, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);
}
