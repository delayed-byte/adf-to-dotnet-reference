using Azure.Storage.Blobs;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.KeyStone.Card;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Faults;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.AcceptanceTests.CardSettlement;

/// <summary>
/// ADR 0010: the Azure Storage SDK retries a failed request three times by default. That retry would sit
/// underneath the orchestrator's <c>RetryPolicy</c> and hide Azurite faults from it, so the card blob store switches it
/// off. The proxy refuses the next connection to Azurite; the store must surface that on the first call, while
/// a client with the SDK's default options (the positive control) absorbs it, which proves the test can tell the two apart.
/// </summary>
public sealed class CardBlobStoreFaultTests(BatchEnvironmentFixture fixture)
{
    /// <summary>The development account key Microsoft publishes for the storage emulator and Azurite; it protects nothing.</summary>
    private const string AzuriteDevelopmentKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refused_storage_connection_reaches_the_caller_on_the_first_call_with_no_SDK_retry_underneath()
    {
        await Env.Blobs.ResetAsync(Ct);
        await using var proxy = StartProxy(out var viaProxy);
        var store = new AzureCardBlobStore(Options.Create(new BlobStorageOptions { ConnectionString = viaProxy }));
        proxy.RefuseNextConnections(1);

        await Should.ThrowAsync<Exception>(() => store.ListChildItemsAsync("card-landing", "2026-11-04", Ct));

        proxy.RefusedConnections.ShouldBe(1);
        proxy.ForwardedConnections.ShouldBe(0, "the SDK did not try again by itself");
        (await store.ListChildItemsAsync("card-landing", "2026-11-04", Ct)).ShouldBeEmpty("the next call, as the orchestrator's retry would make it, goes through");
    }

    [Fact]
    public async Task Positive_control_a_client_with_the_SDK_default_retry_absorbs_the_same_fault()
    {
        await Env.Blobs.ResetAsync(Ct);
        await using var proxy = StartProxy(out var viaProxy);
        var client = new BlobServiceClient(viaProxy);
        proxy.RefuseNextConnections(1);

        var blobs = client.GetBlobContainerClient("card-landing").GetBlobsAsync(cancellationToken: Ct);
        await foreach (var _ in blobs)
        {
            // Nothing is landed; enumerating is what makes the request.
        }

        proxy.RefusedConnections.ShouldBe(1);
        proxy.ForwardedConnections.ShouldBeGreaterThan(0, "the default SDK retried and got through; this is what the store must not do");
    }

    private FaultInjectingTcpProxy StartProxy(out string connectionStringViaProxy)
    {
        var parts = Env.Blobs.ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty, StringComparer.OrdinalIgnoreCase);
        if (parts.ContainsKey("UseDevelopmentStorage"))
        {
            // The compose stack uses the development-storage shortcut, which cannot be pointed at another port: spell the account out.
            var devProxy = FaultInjectingTcpProxy.Start("127.0.0.1", 10000);
            connectionStringViaProxy = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={AzuriteDevelopmentKey};BlobEndpoint=http://{FaultInjectingTcpProxy.Host}:{devProxy.Port}/devstoreaccount1;";
            return devProxy;
        }

        var endpoint = new Uri(parts["BlobEndpoint"]);
        var proxy = FaultInjectingTcpProxy.Start(endpoint.Host, endpoint.Port);
        parts["BlobEndpoint"] = $"http://{FaultInjectingTcpProxy.Host}:{proxy.Port}{endpoint.AbsolutePath}";
        connectionStringViaProxy = string.Join(';', parts.Select(p => p.Key + "=" + p.Value));
        return proxy;
    }
}
