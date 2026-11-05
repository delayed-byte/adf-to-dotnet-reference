using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.TestHarness.Storage;

/// <summary>A blob as found in storage.</summary>
public sealed record StoredBlob(string Container, string Name, byte[] Content, IReadOnlyDictionary<string, string> Metadata);

/// <summary>Blob storage in the Azurite container: the four containers the batch uses, reset and seeded per scenario.</summary>
public sealed class BlobStore(string connectionString)
{
    public static IReadOnlyList<string> Containers { get; } =
        [BatchOracle.LandingContainer, BatchOracle.ArchiveContainer, BatchOracle.QuarantineContainer, BatchOracle.EvidenceContainer];

    public string ConnectionString { get; } = connectionString;

    public BlobServiceClient Client { get; } = new(connectionString);

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        foreach (var name in Containers)
        {
            var container = Client.GetBlobContainerClient(name);
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                await container.DeleteBlobAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task LoadAsync(Scenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        foreach (var blob in scenario.InputBlobs())
        {
            await UploadAsync(blob.Container, blob.Name, blob.Content, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UploadAsync(string container, string name, byte[] content, CancellationToken cancellationToken = default)
    {
        var client = Client.GetBlobContainerClient(container);
        await client.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await client.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(content), overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every blob in the batch's containers, with content and metadata, ordered by path.</summary>
    public async Task<IReadOnlyList<StoredBlob>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<StoredBlob>();
        foreach (var name in Containers)
        {
            var container = Client.GetBlobContainerClient(name);
            if (!await container.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await foreach (var item in container.GetBlobsAsync(new GetBlobsOptions { Traits = BlobTraits.Metadata }, cancellationToken).ConfigureAwait(false))
            {
                var content = await container.GetBlobClient(item.Name).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
                result.Add(new StoredBlob(name, item.Name, content.Value.Content.ToArray(),
                    new SortedDictionary<string, string>(item.Metadata.ToDictionary(), StringComparer.Ordinal)));
            }
        }

        return [.. result.OrderBy(b => b.Container, StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal)];
    }
}
