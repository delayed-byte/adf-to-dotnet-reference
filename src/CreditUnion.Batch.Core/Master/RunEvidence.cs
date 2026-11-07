using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Master;

/// <summary>Input of "Export Run Evidence": the procedure parameters and the sink's <c>retain_until</c> metadata value.</summary>
public sealed record ExportRunEvidenceRequest(Guid BatchRunId, DateOnly BusinessDate, string RetainUntil);

/// <summary>Output of "Export Run Evidence": where the file went (container/blob, as Log Batch Succeeded records it) and how many entries it holds.</summary>
public sealed record RunEvidenceExport(string ObjectName, int EntryCount);

/// <summary>One row of <c>ops.usp_GetRunEvidence</c> (columns <c>section</c>, <c>item</c>, <c>value</c>).</summary>
public sealed record RunEvidenceEntry(string Section, string Item, string? Value);

/// <summary>Where the evidence goes: <see cref="Configuration.BlobStorageOptions.EvidenceContainer"/> (ADF: the literal "evidence").</summary>
public sealed record NightlyBatchSettings(string EvidenceContainer);

/// <summary>A value the evidence export cannot use (an unusable retainUntil). Not retried: the same value fails the same way.</summary>
public sealed class RunEvidenceInputException : Exception
{
    public RunEvidenceInputException()
        : base("The run evidence export input is not valid.")
    {
    }

    public RunEvidenceInputException(string message)
        : base(message)
    {
    }

    public RunEvidenceInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The examiner evidence file of a batch run (PRD, NCUA alignment; ADR 0015): the result set of <c>ops.usp_GetRunEvidence</c>
/// written as ADF's JSON sink writes it (<c>filePattern arrayOfObjects</c>, UTF-8), to
/// <c>evidence/{businessDate}/batch-run-{batchRunId}.json</c> with <c>retain_until</c> metadata.
/// </summary>
public static class RunEvidenceFile
{
    /// <summary>The sink's metadata name (<c>storeSettings.metadata</c>).</summary>
    public const string RetainUntilMetadata = "retain_until";

    public const string ContentType = "application/json";

    /// <summary>Longest retainUntil accepted (a metadata value; the placeholder is "TBD", a policy would put a date).</summary>
    public const int MaxRetainUntilLength = 256;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Dataset <c>DS_Blob_JsonFile</c>: folderPath <c>@variables('resolvedBusinessDate')</c>, fileName <c>@concat('batch-run-', pipeline().RunId, '.json')</c>.</summary>
    public static string BlobName(DateOnly businessDate, Guid batchRunId) =>
        businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/batch-run-" + batchRunId.ToString("D", CultureInfo.InvariantCulture) + ".json";

    /// <summary>Log Batch Succeeded <c>ObjectName</c>: <c>@concat('evidence/', variables('resolvedBusinessDate'), '/batch-run-', pipeline().RunId, '.json')</c>.</summary>
    public static string ObjectName(string container, DateOnly businessDate, Guid batchRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        return container + "/" + BlobName(businessDate, batchRunId);
    }

    /// <summary>The sink's metadata: <c>retain_until = @pipeline().parameters.retainUntil</c>.</summary>
    public static IReadOnlyDictionary<string, string> Metadata(string retainUntil)
    {
        ValidateRetainUntil(retainUntil);
        return new Dictionary<string, string>(StringComparer.Ordinal) { [RetainUntilMetadata] = retainUntil };
    }

    /// <summary>
    /// A blob metadata value is an HTTP header value: printable ASCII, no leading or trailing space. Anything else fails with
    /// <see cref="RunEvidenceInputException"/> before any I/O, instead of a storage error the retry policy would repeat.
    /// </summary>
    public static void ValidateRetainUntil(string? retainUntil)
    {
        if (string.IsNullOrEmpty(retainUntil) || retainUntil.Length > MaxRetainUntilLength
            || retainUntil.Any(c => c < 0x20 || c > 0x7E) || retainUntil.Trim().Length != retainUntil.Length)
        {
            throw new RunEvidenceInputException(
                $"retainUntil must be 1 to {MaxRetainUntilLength} printable ASCII characters without leading or trailing spaces (it becomes the evidence file's {RetainUntilMetadata} metadata).");
        }
    }

    /// <summary>
    /// Streams the entries to <paramref name="destination"/> as a JSON array of <c>{"section", "item", "value"}</c> objects, in
    /// the procedure's order, UTF-8 without a byte-order mark; a NULL value is written as <c>null</c>. Returns the entry count.
    /// Nothing is buffered beyond the writer's own small buffer.
    /// </summary>
    public static async Task<int> WriteJsonAsync(IAsyncEnumerable<RunEvidenceEntry> entries, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(destination);
        var count = 0;
        await using var writer = new Utf8JsonWriter(destination, WriterOptions);
        writer.WriteStartArray();
        await foreach (var entry in entries.WithCancellation(cancellationToken))
        {
            writer.WriteStartObject();
            writer.WriteString("section", entry.Section);
            writer.WriteString("item", entry.Item);
            if (entry.Value is null)
            {
                writer.WriteNull("value");
            }
            else
            {
                writer.WriteString("value", entry.Value);
            }

            writer.WriteEndObject();
            count++;
            if (writer.BytesPending > 16 * 1024)
            {
                await writer.FlushAsync(cancellationToken);
            }
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
        return count;
    }
}

/// <summary>The reporting-store calls of PL_Master_NightlyBatch (login cu_batch_writer).</summary>
public interface INightlyBatchStore
{
    /// <summary><c>ops.usp_LogBatchRunEvent</c>.</summary>
    Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken);

    /// <summary><c>ops.usp_GetRunEvidence</c>, streamed row by row in the procedure's order.</summary>
    IAsyncEnumerable<RunEvidenceEntry> ReadRunEvidenceAsync(Guid batchRunId, DateOnly businessDate, CancellationToken cancellationToken);
}

/// <summary>The evidence sink (LS_Blob_Storage).</summary>
public interface IEvidenceBlobStore
{
    /// <summary>
    /// Writes <paramref name="blobName"/> with the content <paramref name="writeContent"/> streams into the stream it is given. The
    /// blob, with its metadata, appears only once <paramref name="writeContent"/> has completed: a failed attempt leaves no blob
    /// and no partial content, and an existing blob of the same name is replaced only by a complete one (ADR 0015).
    /// </summary>
    Task WriteAsync(string container, string blobName, IReadOnlyDictionary<string, string> metadata, string contentType,
        Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken);
}
