using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.UnitTests.Extract;

/// <summary>An in-memory reporting store that records every call in order.</summary>
public sealed class FakeExtractReportingStore : IExtractReportingStore
{
    private readonly Lock gate = new();
    private readonly List<string> calls = [];
    private readonly List<LogBatchRunEventRequest> events = [];

    public IReadOnlyList<ExtractTableConfig> Tables { get; set; } = [];

    /// <summary>Called for every log request before it is recorded; may throw or wait.</summary>
    public Func<LogBatchRunEventRequest, CancellationToken, Task>? OnLog { get; set; }

    public Func<CancellationToken, Task>? OnMerge { get; set; }

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

    public IReadOnlyList<LogBatchRunEventRequest> Events
    {
        get
        {
            lock (gate)
            {
                return [.. events];
            }
        }
    }

    public async Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        if (OnLog is not null)
        {
            await OnLog(request, cancellationToken);
        }

        lock (gate)
        {
            events.Add(request);
            calls.Add(request.StepName is null ? $"log {request.Status}" : $"log {request.Status} {request.StepName} {request.ObjectName}");
        }
    }

    public Task<IReadOnlyList<ExtractTableConfig>> LookupExtractConfigAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            calls.Add("lookup");
        }

        return Task.FromResult(Tables);
    }

    public async Task MergeKeyStoneAsync(DateOnly businessDate, Guid batchRunId, CancellationToken cancellationToken)
    {
        if (OnMerge is not null)
        {
            await OnMerge(cancellationToken);
        }

        lock (gate)
        {
            calls.Add($"merge {businessDate:yyyy-MM-dd} {batchRunId}");
        }
    }
}

/// <summary>A copier that returns a row count per table, with hooks to delay or fail.</summary>
public sealed class FakeStagingCopier : IKeyStoneStagingCopier
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, int> attempts = new(StringComparer.Ordinal);
    private int running;
    private int maxRunning;

    public Func<ExtractTableConfig, int, CancellationToken, Task>? OnCopy { get; set; }

    public long RowsPerTable { get; set; } = 10;

    public int MaxConcurrent => Volatile.Read(ref maxRunning);

    public int AttemptsFor(string target)
    {
        lock (gate)
        {
            return attempts.GetValueOrDefault(target);
        }
    }

    public async Task<long> CopyToStagingAsync(ExtractTableConfig table, DateOnly businessDate, CancellationToken cancellationToken)
    {
        int attempt;
        lock (gate)
        {
            attempt = attempts[table.TargetName] = attempts.GetValueOrDefault(table.TargetName) + 1;
        }

        var now = Interlocked.Increment(ref running);
        int seen;
        while ((seen = Volatile.Read(ref maxRunning)) < now && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen)
        {
        }

        try
        {
            if (OnCopy is not null)
            {
                await OnCopy(table, attempt, cancellationToken);
            }

            return RowsPerTable;
        }
        finally
        {
            Interlocked.Decrement(ref running);
        }
    }
}

public static class ExtractTestData
{
    public static ExtractTableConfig Table(string name) =>
        new(name, "KS", name.ToUpperInvariant(), "A, B", "A", "D", "stg", "Ks" + name);

    public static IReadOnlyList<ExtractTableConfig> Tables(int count) => [.. Enumerable.Range(1, count).Select(i => Table("T" + i))];
}
