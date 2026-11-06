using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.UnitTests.Card;

/// <summary>An in-memory reporting store: records every call in order and returns a configurable validation result per file.</summary>
public sealed class FakeCardStore : ICardSettlementStore
{
    private readonly Lock gate = new();
    private readonly List<string> calls = [];
    private readonly Dictionary<string, int> attempts = new(StringComparer.Ordinal);
    private int running;
    private int maxRunning;

    public List<LogBatchRunEventRequest> Events { get; } = [];

    public List<LogCardFileCall> CardFileLogs { get; } = [];

    public Dictionary<string, List<string?[]>> Raw { get; } = new(StringComparer.Ordinal);

    public Func<string, SettlementValidation> ValidationFor { get; set; } = file => new SettlementValidation(file, true, 2, 2, 10.00m, 10.00m, 0, null);

    public IReadOnlyList<CardValidationErrorRow> Errors { get; set; } = [];

    /// <summary>Called with (operation, file, attempt) before the operation; may throw or wait.</summary>
    public Func<string, string?, int, CancellationToken, Task>? Hook { get; set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    public int MaxConcurrentLoads => Volatile.Read(ref maxRunning);

    public int AttemptsFor(string operation, string? file = null)
    {
        lock (gate)
        {
            return attempts.GetValueOrDefault(operation + "|" + file);
        }
    }

    public Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken) =>
        Run("log " + request.Status, null, () => Events.Add(request), cancellationToken);

    public Task ResetStagingAsync(DateOnly businessDate, CancellationToken cancellationToken) =>
        Run("reset", null, () => Raw.Clear(), cancellationToken);

    public async Task<long> LoadRawAsync(DateOnly businessDate, string sourceFile, IAsyncEnumerable<string?[]> records, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref running);
        int seen;
        while ((seen = Volatile.Read(ref maxRunning)) < now && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen)
        {
        }

        try
        {
            await Enter("copy", sourceFile, cancellationToken);
            var rows = new List<string?[]>();
            await foreach (var record in records.WithCancellation(cancellationToken))
            {
                rows.Add(record);
            }

            lock (gate)
            {
                Raw[sourceFile] = rows; // the pre-copy delete: a retry replaces the file's rows
            }

            return rows.Count;
        }
        finally
        {
            Interlocked.Decrement(ref running);
        }
    }

    public Task<SettlementValidation> ValidateFileAsync(DateOnly businessDate, string sourceFile, Guid batchRunId, CancellationToken cancellationToken) =>
        Run("validate", sourceFile, () => ValidationFor(sourceFile), cancellationToken);

    public Task PromoteFileAsync(DateOnly businessDate, string sourceFile, CancellationToken cancellationToken) =>
        Run("promote", sourceFile, () => { }, cancellationToken);

    public Task<IReadOnlyList<CardValidationErrorRow>> GetValidationErrorsAsync(DateOnly businessDate, string sourceFile, CancellationToken cancellationToken) =>
        Run("errors", sourceFile, () => Errors, cancellationToken);

    public Task LogCardFileAsync(LogCardFileCall entry, CancellationToken cancellationToken) =>
        Run("logfile " + entry.Outcome, entry.SourceFile, () => CardFileLogs.Add(entry), cancellationToken);

    public Task MergeAsync(DateOnly businessDate, Guid batchRunId, CancellationToken cancellationToken) =>
        Run("merge", null, () => { }, cancellationToken);

    private async Task Enter(string operation, string? file, CancellationToken cancellationToken)
    {
        int attempt;
        lock (gate)
        {
            attempt = attempts[operation + "|" + file] = attempts.GetValueOrDefault(operation + "|" + file) + 1;
            calls.Add(file is null ? operation : operation + " " + file);
        }

        if (Hook is not null)
        {
            await Hook(operation, file, attempt, cancellationToken);
        }
    }

    private async Task Run(string operation, string? file, Action effect, CancellationToken cancellationToken)
    {
        await Enter(operation, file, cancellationToken);
        lock (gate)
        {
            effect();
        }
    }

    private async Task<T> Run<T>(string operation, string? file, Func<T> effect, CancellationToken cancellationToken)
    {
        await Enter(operation, file, cancellationToken);
        lock (gate)
        {
            return effect();
        }
    }
}

/// <summary>An in-memory blob store with the semantics the pipeline relies on: overwrite on copy, idempotent delete, non-recursive listing.</summary>
public sealed class FakeCardBlobStore : ICardBlobStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (byte[] Content, Dictionary<string, string> Metadata)> blobs = new(StringComparer.Ordinal);

    public Func<string, string, CancellationToken, Task>? Hook { get; set; }

    public void Put(string container, string path, string content) =>
        Put(container, path, System.Text.Encoding.UTF8.GetBytes(content));

    public void Put(string container, string path, byte[] content)
    {
        lock (gate)
        {
            blobs[container + "/" + path] = (content, []);
        }
    }

    public bool Exists(string container, string path)
    {
        lock (gate)
        {
            return blobs.ContainsKey(container + "/" + path);
        }
    }

    public byte[] Content(string container, string path)
    {
        lock (gate)
        {
            return blobs[container + "/" + path].Content;
        }
    }

    public IReadOnlyDictionary<string, string> Metadata(string container, string path)
    {
        lock (gate)
        {
            return blobs[container + "/" + path].Metadata;
        }
    }

    public IReadOnlyList<string> Paths(string container)
    {
        lock (gate)
        {
            return [.. blobs.Keys.Where(k => k.StartsWith(container + "/", StringComparison.Ordinal)).Select(k => k[(container.Length + 1)..]).Order(StringComparer.Ordinal)];
        }
    }

    public async Task<IReadOnlyList<LandingItem>> ListChildItemsAsync(string container, string folderPath, CancellationToken cancellationToken)
    {
        await Enter("list", folderPath, cancellationToken);
        var prefix = folderPath.TrimEnd('/') + "/";
        lock (gate)
        {
            var items = new List<LandingItem>();
            foreach (var path in Paths(container).Where(p => p.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('/', StringComparison.Ordinal);
                var item = slash < 0 ? new LandingItem(rest, LandingItem.FileType) : new LandingItem(rest[..slash], LandingItem.FolderType);
                if (!items.Contains(item))
                {
                    items.Add(item);
                }
            }

            return items;
        }
    }

    public async Task<Stream> OpenReadAsync(string container, string path, CancellationToken cancellationToken)
    {
        await Enter("read", path, cancellationToken);
        return new MemoryStream(Content(container, path), writable: false);
    }

    public async Task CopyAsync(string sourceContainer, string sourcePath, string destinationContainer, string destinationPath, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        await Enter("copy", sourcePath, cancellationToken);
        lock (gate)
        {
            blobs[destinationContainer + "/" + destinationPath] = (blobs[sourceContainer + "/" + sourcePath].Content, new Dictionary<string, string>(metadata));
        }
    }

    public async Task DeleteAsync(string container, string path, CancellationToken cancellationToken)
    {
        await Enter("delete", path, cancellationToken);
        lock (gate)
        {
            blobs.Remove(container + "/" + path);
        }
    }

    public async Task WriteAsync(string container, string path, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        await Enter("write", path, cancellationToken);
        lock (gate)
        {
            blobs[container + "/" + path] = (content.ToArray(), []);
        }
    }

    private async Task Enter(string operation, string path, CancellationToken cancellationToken)
    {
        if (Hook is not null)
        {
            await Hook(operation, path, cancellationToken);
        }
    }
}
