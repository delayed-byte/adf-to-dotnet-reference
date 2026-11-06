using System.Globalization;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions;
using CreditUnion.Batch.TestData.Scenarios;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Orchestration;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Stubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.AcceptanceTests.Support;

/// <summary>One pipeline run as the test saw it: the orchestration result plus what reached the KeyBridge and webhook stubs.</summary>
public sealed record PipelineRun(
    string Pipeline,
    DateOnly BusinessDate,
    Guid BatchRunId,
    OrchestrationResult Result,
    IReadOnlyList<StubRequest> KeyBridgeRequests,
    IReadOnlyList<string> Alerts)
{
    public bool Succeeded => Result.Succeeded;

    /// <summary>The ADF error code of a failed run (PipelineFailedException contract, ADR 0008); null otherwise.</summary>
    public string? ErrorCode => Result.Failure is { } f && PipelineFailedException.TryParse(f.ErrorType, f.ErrorMessage, out var code, out _) ? code : null;

    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Pipeline} for {BusinessDate:yyyy-MM-dd}, batch run {BatchRunId}: {Result.Describe()}");
}

/// <summary>
/// The batch as an acceptance test drives it (ADR 0008): the Functions app's own service registrations
/// (<see cref="BatchServiceCollectionExtensions.AddCreditUnionBatch"/>) configured against the test
/// containers and the in-process KeyBridge and webhook stubs, hosted by <see cref="InProcessFunctionHost"/>
/// on a virtual clock, with every log line and span captured for AT-17. One per test.
/// </summary>
public sealed class BatchHost : IAsyncDisposable
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private readonly ServiceProvider services;

    private BatchHost(BatchEnvironment environment, Scenario scenario, string? keyStoneConnectionString)
    {
        Environment = environment;
        Scenario = scenario;
        KeyBridge = new KeyBridgeStub(scenario.KeyBridgeMappingJson, environment.Secrets.KeyBridgePassword);
        Webhook = new AlertWebhookStub(environment.Secrets.AlertWebhookToken);
        Logs = new LogCapture();
        Clock = new VirtualClock(DefaultStart(DateOnly.ParseExact(scenario.Runs[0].BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{KeyStoneDb2Options.SectionName}:{nameof(KeyStoneDb2Options.ConnectionString)}"] = keyStoneConnectionString ?? environment.KeyStone.ExtractConnectionString,
            [$"{ReportingSqlOptions.SectionName}:{nameof(ReportingSqlOptions.ConnectionString)}"] = environment.Reporting.WriterConnectionString,
            [$"{BlobStorageOptions.SectionName}:{nameof(BlobStorageOptions.ConnectionString)}"] = environment.Blobs.ConnectionString,
            [$"{KeyBridgeOptions.SectionName}:{nameof(KeyBridgeOptions.BaseUrl)}"] = KeyBridge.BaseUrl,
            [$"{KeyBridgeOptions.SectionName}:{nameof(KeyBridgeOptions.Username)}"] = KeyBridgeStub.Username,
            [$"{KeyBridgeOptions.SectionName}:{nameof(KeyBridgeOptions.Password)}"] = environment.Secrets.KeyBridgePassword,
            [$"{ReconAlertWebhookOptions.SectionName}:{nameof(ReconAlertWebhookOptions.Url)}"] = Webhook.Url,
        }).Build();

        var collection = new ServiceCollection();
        collection.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddCreditUnionBatch(configuration);
        collection.AddSingleton<TimeProvider>(Clock);
        services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Functions = new InProcessFunctionHost(FunctionCatalog.FromAssembly(typeof(BatchServiceCollectionExtensions).Assembly), services, Clock);
    }

    public BatchEnvironment Environment { get; }

    public Scenario Scenario { get; }

    public KeyBridgeStub KeyBridge { get; }

    public AlertWebhookStub Webhook { get; }

    public LogCapture Logs { get; }

    public VirtualClock Clock { get; }

    public InProcessFunctionHost Functions { get; }

    public OrchestrationTrace Trace => Functions.Trace;

    /// <param name="keyStoneConnectionString">Overrides the KeyStone connection (AT-04 routes it through a fault proxy).</param>
    public static BatchHost Start(BatchEnvironment environment, Scenario scenario, string? keyStoneConnectionString = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(scenario);
        return new BatchHost(environment, scenario, keyStoneConnectionString);
    }

    /// <summary>
    /// Runs one pipeline entry point as the master or an operator would start it. The master's
    /// instance ID is <paramref name="batchRunId"/>; a child gets its own instance ID and receives the
    /// batch run ID as input. The clock starts at <paramref name="triggerTimeUtc"/> or, for a manual
    /// run, at 03:00 Pacific on the day after the business date.
    /// </summary>
    public Task<PipelineRun> RunAsync(string pipeline, DateOnly businessDate, Guid batchRunId, decimal tolerance = 0.00m, DateTimeOffset? triggerTimeUtc = null)
    {
        Clock.Set(triggerTimeUtc ?? DefaultStart(businessDate));
        object input = pipeline switch
        {
            PipelineNames.Master => new NightlyBatchInput { BusinessDate = businessDate, ReconTolerance = tolerance },
            PipelineNames.Reconciliation => new ReconciliationInput(businessDate, batchRunId, tolerance),
            _ => new PipelineRunInput(businessDate, batchRunId),
        };
        var instanceId = pipeline == PipelineNames.Master ? batchRunId.ToString("D") : Guid.NewGuid().ToString("N");
        return ObserveAsync(pipeline, businessDate, batchRunId, () => Functions.RunOrchestrationAsync(pipeline, input, instanceId));
    }

    /// <summary>Runs scenario run <paramref name="run"/> (its pipeline, business date, tolerance and trigger time).</summary>
    public Task<PipelineRun> RunAsync(RunInput run, Guid batchRunId)
    {
        ArgumentNullException.ThrowIfNull(run);
        return RunAsync(run.Pipeline, run.Date(), batchRunId, decimal.Parse(run.Tolerance, NumberStyles.Number, CultureInfo.InvariantCulture),
            run.TriggerTimeUtc is { } t ? DateTimeOffset.Parse(t, CultureInfo.InvariantCulture) : null);
    }

    /// <summary>Runs every run of the scenario in order; <paramref name="runIds"/> are the batch run IDs, run 1 first.</summary>
    public async Task<IReadOnlyList<PipelineRun>> RunScenarioAsync(IReadOnlyList<Guid> runIds)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        var results = new List<PipelineRun>();
        foreach (var run in Scenario.Runs)
        {
            results.Add(await RunAsync(run, runIds[run.Run - 1]));
        }

        return results;
    }

    /// <summary>Runs an orchestration the timer trigger scheduled, as the Durable backend would start it.</summary>
    public Task<PipelineRun> RunScheduledAsync(ScheduledOrchestration scheduled, DateTimeOffset fireTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(scheduled);
        if (!Guid.TryParse(scheduled.InstanceId, out var batchRunId))
        {
            throw new InvalidOperationException(
                $"The trigger started {scheduled.Name} with instance ID '{scheduled.InstanceId}'. The master's instance ID is its batchRunId and must be a GUID (ADR 0008).");
        }

        var input = Functions.Converter.Deserialize<NightlyBatchInput>(scheduled.SerializedInput)
            ?? throw new InvalidOperationException($"The trigger started {scheduled.Name} without input.");
        Clock.Set(fireTimeUtc);
        return ObserveAsync(scheduled.Name, input.BusinessDate ?? default, batchRunId, () => Functions.RunScheduledAsync(scheduled));
    }

    public async ValueTask DisposeAsync()
    {
        KeyBridge.Dispose();
        Webhook.Dispose();
        Logs.Dispose();
        await services.DisposeAsync();
    }

    private static DateTimeOffset DefaultStart(DateOnly businessDate)
    {
        var local = businessDate.AddDays(1).ToDateTime(new TimeOnly(3, 0));
        return new DateTimeOffset(local, Pacific.GetUtcOffset(local)).ToUniversalTime();
    }

    private async Task<PipelineRun> ObserveAsync(string pipeline, DateOnly businessDate, Guid batchRunId, Func<Task<OrchestrationResult>> run)
    {
        var keyBridgeBefore = KeyBridge.Requests.Count;
        var alertsBefore = Webhook.Alerts.Count;
        var result = await run();
        for (var failure = result.Failure; failure is not null; failure = failure.InnerFailure)
        {
            // The Functions host logs orchestration failures, so their text is held to the AT-17 rules too.
            Logs.AddHostText($"failure details of {pipeline}", $"{failure.ErrorType}: {failure.ErrorMessage}\n{failure.StackTrace}");
        }

        return new PipelineRun(pipeline, businessDate, batchRunId, result,
            [.. KeyBridge.Requests.Skip(keyBridgeBefore).Where(r => r.Path == KeyBridgeStub.MappingsPath)], [.. Webhook.Alerts.Skip(alertsBefore)]);
    }
}

public static class RunInputExtensions
{
    public static DateOnly Date(this RunInput run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return DateOnly.ParseExact(run.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
