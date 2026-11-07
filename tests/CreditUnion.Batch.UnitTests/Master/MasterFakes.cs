using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.Functions;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Orchestration;
using CreditUnion.Batch.UnitTests.Card;
using CreditUnion.Batch.UnitTests.Extract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.UnitTests.Master;

/// <summary>
/// The whole batch in memory: the production registrations (<see cref="BatchServiceCollectionExtensions.AddCreditUnionBatch"/>)
/// with every store and client replaced by a fake, hosted by the in-process Durable host on a virtual clock. The master runs
/// its real children as sub-orchestrations, so failures can be injected where they really happen.
/// </summary>
public sealed class MasterWorld : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);
    private readonly ServiceProvider services;

    public MasterWorld()
    {
        Clock = new VirtualClock(Start);
        var collection = new ServiceCollection();
        collection.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        collection.AddCreditUnionBatch(new ConfigurationBuilder().Build());
        collection.AddSingleton<TimeProvider>(Clock);
        collection.AddSingleton<IExtractReportingStore>(Extract);
        collection.AddSingleton<IKeyStoneStagingCopier>(Copier);
        collection.AddSingleton<ICardSettlementStore>(Card);
        collection.AddSingleton<ICardBlobStore>(CardBlobs);
        collection.AddSingleton<IKeyBridgeClient>(KeyBridge);
        collection.AddSingleton<IMapAndAggregateStore>(Map);
        collection.AddSingleton<IReconciliationStore>(Recon);
        collection.AddSingleton<IReconAlertClient>(Alerts);
        collection.AddSingleton<INightlyBatchStore>(Store);
        collection.AddSingleton<IEvidenceBlobStore>(Evidence);
        services = collection.BuildServiceProvider();
        Host = new InProcessFunctionHost(FunctionCatalog.FromAssembly(typeof(BatchServiceCollectionExtensions).Assembly), services, Clock);
    }

    public LogCapture Logs { get; } = new();

    public VirtualClock Clock { get; }

    public InProcessFunctionHost Host { get; }

    public FakeExtractReportingStore Extract { get; } = new() { Tables = ExtractTestData.Tables(3) };

    public FakeStagingCopier Copier { get; } = new();

    public FakeCardStore Card { get; } = new();

    public FakeCardBlobStore CardBlobs { get; } = new();

    public StubKeyBridge KeyBridge { get; } = new();

    public StubMapStore Map { get; } = new();

    public StubReconStore Recon { get; } = new();

    public StubAlertClient Alerts { get; } = new();

    public FakeNightlyBatchStore Store { get; } = new();

    public FakeEvidenceBlobStore Evidence { get; } = new();

    public Task<OrchestrationResult> RunMasterAsync(NightlyBatchInput input, Guid batchRunId) =>
        Host.RunOrchestrationAsync(PipelineNames.Master, input, batchRunId.ToString("D"));

    public void Dispose()
    {
        Logs.Dispose();
        services.Dispose();
    }
}

public sealed class StubKeyBridge : IKeyBridgeClient
{
    public ConcurrentQueue<Guid> Calls { get; } = new();

    public Task<KeyBridgeGlMappings> GetGlMappingsAsync(Guid batchRunId, CancellationToken cancellationToken)
    {
        Calls.Enqueue(batchRunId);
        return Task.FromResult(new KeyBridgeGlMappings("2026-11-01", "[]"));
    }
}

public sealed class StubMapStore : IMapAndAggregateStore
{
    public ConcurrentQueue<LogBatchRunEventRequest> Events { get; } = new();

    public UnmappedTxnCodes Unmapped { get; set; } = new(0, string.Empty);

    public Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        Events.Enqueue(request);
        return Task.CompletedTask;
    }

    public Task UpsertGlMappingAsync(CacheGlMappingRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<UnmappedTxnCodes> GetUnmappedTxnCodesAsync(DateOnly businessDate, CancellationToken cancellationToken) => Task.FromResult(Unmapped);

    public Task<IReadOnlyList<GlMapping>> ReadGlMapAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GlMapping>>([]);

    public async IAsyncEnumerable<PostedAmount> ReadPostedAmountsAsync(DateOnly businessDate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }

    public Task ReplaceDailyGlTotalsAsync(DateOnly businessDate, IReadOnlyList<DailyGlTotal> totals, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class StubReconStore : IReconciliationStore
{
    public ConcurrentQueue<LogBatchRunEventRequest> Events { get; } = new();

    public ConcurrentQueue<ReconQuery> Queries { get; } = new();

    public bool AlertRequired { get; set; }

    public int Marked { get; private set; }

    public Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        Events.Enqueue(request);
        return Task.CompletedTask;
    }

    public Task<CardReconTotals> GetCardTotalsAsync(ReconQuery query, CancellationToken cancellationToken)
    {
        Queries.Enqueue(query);
        return Task.FromResult(new CardReconTotals(10.00m, AlertRequired ? 9.99m : 10.00m, AlertRequired ? 0.01m : 0.00m, AlertRequired ? 0.01m : 0.00m, AlertRequired));
    }

    public Task<GlReconTotals> GetGlTotalsAsync(ReconQuery query, CancellationToken cancellationToken) => Task.FromResult(new GlReconTotals(2, 0, 0.00m, false));

    public Task<ReconExceptionResult> RecordExceptionsAsync(RecordReconExceptionsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ReconExceptionResult(AlertRequired, AlertRequired ? 1 : 0, AlertRequired ? 1 : 0, AlertRequired, AlertRequired ? "CardSettlement ALL difference 0.01" : string.Empty));

    public Task MarkAlertSentAsync(MarkReconAlertSentRequest request, CancellationToken cancellationToken)
    {
        Marked++;
        return Task.CompletedTask;
    }
}

public sealed class StubAlertClient : IReconAlertClient
{
    public bool Down { get; set; }

    public int Attempts { get; private set; }

    public Task PostAsync(string jsonBody, CancellationToken cancellationToken)
    {
        Attempts++;
        return Down ? Task.FromException(new ReconAlertUnavailableException("The reconciliation alert webhook answered 503.")) : Task.CompletedTask;
    }
}

/// <summary>The master's reporting-store calls: records every event; failures can be injected per call.</summary>
public sealed class FakeNightlyBatchStore : INightlyBatchStore
{
    private readonly ConcurrentQueue<LogBatchRunEventRequest> events = new();

    public IReadOnlyList<LogBatchRunEventRequest> Events => [.. events];

    /// <summary>Returns an exception to throw for a log request, or null to record it.</summary>
    public Func<LogBatchRunEventRequest, Exception?> FailLog { get; set; } = _ => null;

    public IReadOnlyList<RunEvidenceEntry> Evidence { get; set; } =
    [
        new("Run", "batchRunId", "x"),
        new("RowCounts", "core.PostedTransaction", "3"),
    ];

    /// <summary>Thrown after the first evidence row, on the given (1-based) read attempts.</summary>
    public Func<int, Exception?> FailEvidenceRead { get; set; } = _ => null;

    public int EvidenceReads { get; private set; }

    public Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        if (FailLog(request) is { } failure)
        {
            return Task.FromException(failure);
        }

        events.Enqueue(request);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RunEvidenceEntry> ReadRunEvidenceAsync(Guid batchRunId, DateOnly businessDate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var attempt = ++EvidenceReads;
        for (var i = 0; i < Evidence.Count; i++)
        {
            await Task.Yield();
            if (i == 1 && FailEvidenceRead(attempt) is { } failure)
            {
                throw failure;
            }

            yield return Evidence[i];
        }
    }
}

/// <summary>An evidence sink with the contract of <see cref="IEvidenceBlobStore"/>: a blob appears only when its content was written completely.</summary>
public sealed class FakeEvidenceBlobStore : IEvidenceBlobStore
{
    public ConcurrentDictionary<string, (byte[] Content, IReadOnlyDictionary<string, string> Metadata, string ContentType)> Blobs { get; } = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public async Task WriteAsync(string container, string blobName, IReadOnlyDictionary<string, string> metadata, string contentType,
        Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken)
    {
        Writes++;
        using var buffer = new MemoryStream();
        await writeContent(buffer, cancellationToken);
        Blobs[container + "/" + blobName] = (buffer.ToArray(), new Dictionary<string, string>(metadata, StringComparer.Ordinal), contentType);
    }
}
