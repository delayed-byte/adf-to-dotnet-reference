using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Activities;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CreditUnion.Batch.UnitTests.Map;

/// <summary>
/// PL_MapAndAggregate as the in-process Durable host runs it (ADR 0008), with the reporting store and KeyBridge replaced
/// by fakes: the ADF activity order, the If Condition and Fail activity, retries per the ADF policy on virtual time, and
/// which failures are not retried. AT-08 to AT-11 run the same orchestrator against real containers.
/// </summary>
public sealed class MapOrchestratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);
    private static readonly PipelineRunInput Input = new(new DateOnly(2026, 11, 4), Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private static readonly AdfActivityPolicy WebPolicy = AdfActivityPolicy.Read(PipelineNames.MapAndAggregate, "Get GL Mapping From KeyBridge");

    private readonly FakeMapStore store = new();
    private readonly FakeKeyBridge keyBridge = new();
    private readonly VirtualClock clock = new(Start);
    private readonly InProcessFunctionHost host;

    public MapOrchestratorTests()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IMapAndAggregateStore>(store)
            .AddSingleton<IKeyBridgeClient>(keyBridge)
            .AddSingleton(MapActivityPolicies.Adf)
            .AddTransient<MapAndAggregateSteps>()
            .BuildServiceProvider();
        host = new InProcessFunctionHost(FunctionCatalog.FromAssembly(typeof(MapAndAggregateActivities).Assembly), services, clock);
    }

    private Task<OrchestrationResult> RunAsync() => host.RunOrchestrationAsync(PipelineNames.MapAndAggregate, Input);

    [Fact]
    public async Task Every_code_mapped_runs_the_ADF_activities_in_order_and_replaces_the_dates_totals()
    {
        store.Transactions = [new PostedAmount(1, "DEP1", Input.BusinessDate, 100.00m), new PostedAmount(2, "DEP1", Input.BusinessDate, 0.50m)];

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Calls.ShouldBe(["log Started", "cache 2026-11-01", "lookup", "read map", "read transactions", "replace 2 row(s)", "log Succeeded"]);
        keyBridge.Calls.ShouldBe([Input.BatchRunId]);
        store.Cached.ShouldHaveSingleItem().ShouldBe(new CacheGlMappingRequest(Input.BatchRunId, FakeKeyBridge.Mappings, "2026-11-01"));
        store.Replaced.ShouldBe([
            new DailyGlTotal(Input.BusinessDate, "1010      ", 100.50m, 0.00m, 100.50m, 2),
            new DailyGlTotal(Input.BusinessDate, "2100      ", 0.00m, 100.50m, -100.50m, 2)]);
        store.Events[0].ShouldBe(new LogBatchRunEventRequest(Input.BatchRunId, Input.BusinessDate, PipelineNames.MapAndAggregate, "Started", StartedUtc: Start.UtcDateTime));
        store.Events[1].ShouldBe(new LogBatchRunEventRequest(Input.BatchRunId, Input.BusinessDate, PipelineNames.MapAndAggregate, "Succeeded",
            ObjectName: "core.DailyGlTotals", EndedUtc: Start.UtcDateTime));
        host.Trace.RetryDecisions.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unmapped_code_logs_the_Failed_event_then_fails_with_TXN_CODE_UNMAPPED_without_retrying_or_aggregating()
    {
        store.Unmapped = new UnmappedTxnCodes(2, "P2PD,ZZZZ");

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        PipelineFailedException.TryParse(result.Failure!.ErrorType, result.Failure.ErrorMessage, out var code, out var detail).ShouldBeTrue(result.Describe());
        code.ShouldBe(PipelineErrorCodes.TxnCodeUnmapped);
        detail.ShouldBe("Unmapped transaction codes: P2PD,ZZZZ");
        store.Calls.ShouldBe(["log Started", "cache 2026-11-01", "lookup", "log Failed"]);
        store.Events[1].ShouldBe(new LogBatchRunEventRequest(Input.BatchRunId, Input.BusinessDate, PipelineNames.MapAndAggregate, "Failed",
            StepName: "CheckTxnCodeMapping", RecordCount: 2, ErrorCode: "TXN_CODE_UNMAPPED", ErrorMessage: "Unmapped transaction codes: P2PD,ZZZZ"));
        store.Replaced.ShouldBeNull("nothing is aggregated");
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).ShouldBeEmpty("a validation failure is not retried");
    }

    [Fact]
    public async Task The_Fail_activity_runs_even_when_the_Failed_event_cannot_be_logged_as_its_Completed_dependency_says()
    {
        store.Unmapped = new UnmappedTxnCodes(1, "P2PD");
        store.FailLog = status => status == "Failed" ? new TimeoutException("sql down") : null;

        var result = await RunAsync();

        PipelineFailedException.TryParse(result.Failure!.ErrorType, result.Failure.ErrorMessage, out var code, out var detail).ShouldBeTrue(result.Describe());
        code.ShouldBe(PipelineErrorCodes.TxnCodeUnmapped);
        detail.ShouldBe("Unmapped transaction codes: P2PD");
        host.Trace.Attempts.Count(a => a.Name == MapActivityNames.LogUnmapped).ShouldBe(MapActivityPolicies.Adf.LogUnmapped.MaxNumberOfAttempts, "the log itself is retried per its policy");
    }

    [Fact]
    public async Task KeyBridge_503_twice_is_retried_thirty_seconds_apart_and_succeeds()
    {
        keyBridge.Failures.Enqueue(new KeyBridgeUnavailableException("HTTP 503"));
        keyBridge.Failures.Enqueue(new KeyBridgeUnavailableException("HTTP 503"));

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        keyBridge.Calls.Count.ShouldBe(3);
        var failed = host.Trace.FailedAttempts;
        failed.Count.ShouldBe(2);
        failed.ShouldAllBe(a => a.Name == MapActivityNames.GetGlMapping && WebPolicy.IsImplementedBy(a.Policy));
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).Select(d => d.Delay).ShouldAllBe(d => d == TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task KeyBridge_down_on_all_four_attempts_fails_the_run_before_the_cache_is_touched()
    {
        for (var i = 0; i < 4; i++)
        {
            keyBridge.Failures.Enqueue(new TimeoutException("no answer"));
        }

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        keyBridge.Calls.Count.ShouldBe(4, "retry 3 = four attempts");
        store.Calls.ShouldBe(["log Started"], "no Failed event: the ADF pipeline has no failure branch for the Web activity");
    }

    [Theory]
    [InlineData(typeof(KeyBridgeRejectedException))]
    [InlineData(typeof(KeyBridgeResponseException))]
    [InlineData(typeof(KeyBridgeConfigurationException))]
    public async Task A_rejected_request_or_unusable_response_is_not_retried(Type exception)
    {
        keyBridge.Failures.Enqueue((Exception)Activator.CreateInstance(exception, "refused")!);

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        keyBridge.Calls.Count.ShouldBe(1);
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).ShouldBeEmpty();
        store.Calls.ShouldBe(["log Started"]);
    }

    [Fact]
    public async Task A_mapping_the_procedure_rejects_is_not_retried_and_nothing_after_it_runs()
    {
        store.FailCache = new GlMappingInvalidException("KEYBRIDGE_MAPPING_INVALID: mapping set is empty.");

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        host.Trace.Attempts.Count(a => a.Name == MapActivityNames.CacheGlMapping).ShouldBe(1);
        store.Calls.ShouldBe(["log Started", "cache 2026-11-01"]);
    }

    [Fact]
    public async Task A_transient_aggregation_failure_is_retried_once_after_sixty_seconds()
    {
        store.FailReplaceOnce = true;

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        var attempts = host.Trace.Attempts.Where(a => a.Name == MapActivityNames.Aggregate).ToList();
        attempts.Count.ShouldBe(2);
        host.Trace.RetryDecisionAfter(attempts[0])!.Delay.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_transaction_that_lost_its_mapping_after_the_check_fails_the_aggregation_without_retry_or_write()
    {
        store.Transactions = [new PostedAmount(1, "NEW1", Input.BusinessDate, 1.00m)];

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(GlAggregationException).FullName);
        host.Trace.Attempts.Count(a => a.Name == MapActivityNames.Aggregate).ShouldBe(1);
        store.Replaced.ShouldBeNull();
    }

    private sealed class FakeKeyBridge : IKeyBridgeClient
    {
        public const string Mappings = "[{\"transactionCode\":\"DEP1\"}]";

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public List<Guid> Calls { get; } = [];

        public Task<KeyBridgeGlMappings> GetGlMappingsAsync(Guid batchRunId, CancellationToken cancellationToken)
        {
            Calls.Add(batchRunId);
            return Failures.TryDequeue(out var failure) ? Task.FromException<KeyBridgeGlMappings>(failure) : Task.FromResult(new KeyBridgeGlMappings("2026-11-01", Mappings));
        }
    }

    private sealed class FakeMapStore : IMapAndAggregateStore
    {
        public List<string> Calls { get; } = [];

        public List<LogBatchRunEventRequest> Events { get; } = [];

        public List<CacheGlMappingRequest> Cached { get; } = [];

        public IReadOnlyList<DailyGlTotal>? Replaced { get; private set; }

        public UnmappedTxnCodes Unmapped { get; set; } = new(0, string.Empty);

        public IReadOnlyList<PostedAmount> Transactions { get; set; } = [];

        public Func<string, Exception?> FailLog { get; set; } = _ => null;

        public Exception? FailCache { get; set; }

        public bool FailReplaceOnce { get; set; }

        public Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
        {
            if (FailLog(request.Status) is { } failure)
            {
                return Task.FromException(failure);
            }

            Calls.Add("log " + request.Status);
            Events.Add(request);
            return Task.CompletedTask;
        }

        public Task UpsertGlMappingAsync(CacheGlMappingRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("cache " + request.SourceVersion);
            if (FailCache is { } failure)
            {
                return Task.FromException(failure);
            }

            Cached.Add(request);
            return Task.CompletedTask;
        }

        public Task<UnmappedTxnCodes> GetUnmappedTxnCodesAsync(DateOnly businessDate, CancellationToken cancellationToken)
        {
            Calls.Add("lookup");
            return Task.FromResult(Unmapped);
        }

        public Task<IReadOnlyList<GlMapping>> ReadGlMapAsync(CancellationToken cancellationToken)
        {
            Calls.Add("read map");
            return Task.FromResult<IReadOnlyList<GlMapping>>([new GlMapping("DEP1", "1010      ", "D", 1m), new GlMapping("DEP1", "2100      ", "C", 1m)]);
        }

        public async IAsyncEnumerable<PostedAmount> ReadPostedAmountsAsync(DateOnly businessDate, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add("read transactions");
            foreach (var t in Transactions)
            {
                await Task.Yield();
                yield return t;
            }
        }

        public Task ReplaceDailyGlTotalsAsync(DateOnly businessDate, IReadOnlyList<DailyGlTotal> totals, CancellationToken cancellationToken)
        {
            if (FailReplaceOnce)
            {
                FailReplaceOnce = false;
                return Task.FromException(new TimeoutException("deadlock victim"));
            }

            Calls.Add($"replace {totals.Count} row(s)");
            Replaced = totals;
            return Task.CompletedTask;
        }
    }
}
