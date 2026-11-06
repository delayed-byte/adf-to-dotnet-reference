using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.KeyStone;
using CreditUnion.Batch.KeyStone.Card;
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
        return services;
    }
}
