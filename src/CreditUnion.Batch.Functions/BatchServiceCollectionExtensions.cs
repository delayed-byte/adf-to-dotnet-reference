using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.KeyStone;
using CreditUnion.Batch.KeyStone.Card;
using CreditUnion.Batch.KeyStone.KeyBridge;
using CreditUnion.Batch.KeyStone.Map;
using CreditUnion.Batch.KeyStone.Reconciliation;
using CreditUnion.Batch.KeyStone.Reporting;
using CreditUnion.Batch.KeyStone.Staging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.Functions;

/// <summary>
/// Composition root of the batch: everything the orchestrators, activities and the timer trigger
/// resolve. The Functions host (Program.cs) and the acceptance-test host call the same method, so
/// the tests exercise the production wiring (ADR 0008).
/// </summary>
public static class BatchServiceCollectionExtensions
{
    public static IServiceCollection AddCreditUnionBatch(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The Db2 driver's own connection retry must be off before the first Db2 connection (ADR 0009).
        Db2DriverConfiguration.DisableClientReroute(configuration[$"{KeyStoneDb2Options.SectionName}:{nameof(KeyStoneDb2Options.ConnectionString)}"]);

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<KeyStoneDb2Options>().Bind(configuration.GetSection(KeyStoneDb2Options.SectionName));
        services.AddOptions<ReportingSqlOptions>().Bind(configuration.GetSection(ReportingSqlOptions.SectionName));
        services.AddOptions<BlobStorageOptions>().Bind(configuration.GetSection(BlobStorageOptions.SectionName));
        services.AddOptions<KeyBridgeOptions>().Bind(configuration.GetSection(KeyBridgeOptions.SectionName));
        services.AddOptions<ReconAlertWebhookOptions>().Bind(configuration.GetSection(ReconAlertWebhookOptions.SectionName));

        // PL_ExtractKeyStone: policies are the ADF values; the services hold the work, the activities only call them.
        services.TryAddSingleton(ExtractActivityPolicies.Adf);
        services.TryAddSingleton<IExtractReportingStore, SqlExtractReportingStore>();
        services.TryAddSingleton<IKeyStoneStagingCopier, Db2StagingCopier>();
        services.TryAddSingleton<ExtractKeyStoneSteps>();

        // PL_IngestCardSettlement: containers and retainUntil are configuration, not input (ADR 0008).
        services.TryAddSingleton(CardActivityPolicies.Adf);
        services.TryAddSingleton(provider =>
        {
            var blob = provider.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
            return new CardSettlementSettings(blob.LandingContainer, blob.ArchiveContainer, blob.QuarantineContainer, blob.RetainUntil);
        });
        services.TryAddSingleton<ICardSettlementStore, SqlCardSettlementStore>();
        services.TryAddSingleton<ICardBlobStore, AzureCardBlobStore>();
        services.TryAddSingleton<CardSettlementSteps>();

        // PL_MapAndAggregate (ADR 0011, ADR 0012): keyBridgeBaseUrl and the credential are configuration (KeyBridgeOptions).
        services.TryAddSingleton(MapActivityPolicies.Adf);
        services.TryAddSingleton<IMapAndAggregateStore, SqlMapAndAggregateStore>();
        AddKeyBridgeClient(services);
        services.TryAddTransient<MapAndAggregateSteps>();

        // PL_Reconciliation (ADR 0013): the webhook URL is configuration (ReconAlertWebhookOptions) and is resolved inside the
        // posting client. No Db2 and no blob is read, so Db2DriverConfiguration and the Storage retry settings are not involved.
        services.TryAddSingleton(ReconActivityPolicies.Adf);
        services.TryAddSingleton<IReconciliationStore, SqlReconciliationStore>();
        AddReconAlertClient(services);
        services.TryAddTransient<ReconciliationSteps>();
        return services;
    }

    /// <summary>
    /// The typed alert-webhook client: <c>httpRequestTimeout</c> per request, no redirects, no cookies, no retry handler (the
    /// Durable RetryPolicy is the only retry layer, ADR 0008), no HTTP-client loggers and no trace spans. The webhook URL carries
    /// a secret token in its path; .NET redacts only query strings, and the client's span would record the full URL as
    /// <c>url.full</c>, so the span is switched off for this client by removing its activity-headers propagator (ADR 0013).
    /// </summary>
    public static IHttpClientBuilder AddReconAlertClient(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddHttpClient<IReconAlertClient, HttpReconAlertClient>(client => client.Timeout = ReconAlertWebActivity.HttpRequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ActivityHeadersPropagator = null })
            .RemoveAllLoggers();
    }

    /// <summary>
    /// The typed KeyBridge client: <c>httpRequestTimeout</c> per request, no redirects (the Basic credential goes to the
    /// configured host only), no cookies, no retry handler (the Durable RetryPolicy is the only retry layer, ADR 0008) and
    /// no HTTP-client loggers, so request URLs and headers are never logged by the factory (ADR 0012).
    /// </summary>
    public static IHttpClientBuilder AddKeyBridgeClient(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddHttpClient<IKeyBridgeClient, HttpKeyBridgeClient>(client => client.Timeout = KeyBridgeWebActivity.HttpRequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
    }
}
