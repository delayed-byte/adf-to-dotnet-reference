using CreditUnion.Batch.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<KeyStoneDb2Options>().Bind(configuration.GetSection(KeyStoneDb2Options.SectionName));
        services.AddOptions<ReportingSqlOptions>().Bind(configuration.GetSection(ReportingSqlOptions.SectionName));
        services.AddOptions<BlobStorageOptions>().Bind(configuration.GetSection(BlobStorageOptions.SectionName));
        services.AddOptions<KeyBridgeOptions>().Bind(configuration.GetSection(KeyBridgeOptions.SectionName));
        services.AddOptions<ReconAlertWebhookOptions>().Bind(configuration.GetSection(ReconAlertWebhookOptions.SectionName));
        return services;
    }
}
