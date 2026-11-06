using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Configuration;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Card;

/// <summary>
/// Blob storage for PL_IngestCardSettlement (LS_Blob_Storage). The Azure Storage SDK retries a failed request
/// three times by default; <see cref="CreateClientOptions"/> turns that off so that a storage fault reaches the
/// orchestrator's <c>RetryPolicy</c> and the audit trail instead of being absorbed underneath it (ADR 0010).
/// The connection string holds the account key and is never logged.
/// </summary>
public sealed class AzureCardBlobStore(IOptions<BlobStorageOptions> options) : ICardBlobStore
{
    private readonly Lazy<BlobServiceClient> service = new(() => new BlobServiceClient(options.Value.ConnectionString, CreateClientOptions()));

    /// <summary>Client options with the SDK's retry policy disabled (<c>MaxRetries = 0</c>).</summary>
    public static BlobClientOptions CreateClientOptions()
    {
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.MaxRetries = 0;
        return clientOptions;
    }

    public async Task<IReadOnlyList<LandingItem>> ListChildItemsAsync(string container, string folderPath, CancellationToken cancellationToken)
    {
        var client = service.Value.GetBlobContainerClient(container);
        var prefix = folderPath.TrimEnd('/') + "/";
        var items = new List<LandingItem>();
        await foreach (var item in client.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", prefix, cancellationToken))
        {
            if (item.IsPrefix)
            {
                items.Add(new LandingItem(NameWithin(item.Prefix, prefix), LandingItem.FolderType));
            }
            else
            {
                items.Add(new LandingItem(NameWithin(item.Blob.Name, prefix), LandingItem.FileType));
            }
        }

        return items;
    }

    public async Task<Stream> OpenReadAsync(string container, string path, CancellationToken cancellationToken) =>
        await service.Value.GetBlobContainerClient(container).GetBlobClient(path).OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), cancellationToken);

    public async Task CopyAsync(string sourceContainer, string sourcePath, string destinationContainer, string destinationPath, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var source = service.Value.GetBlobContainerClient(sourceContainer).GetBlobClient(sourcePath);
        var destinationContainerClient = service.Value.GetBlobContainerClient(destinationContainer);
        await destinationContainerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var destination = destinationContainerClient.GetBlobClient(destinationPath);

        var properties = await source.GetPropertiesAsync(cancellationToken: cancellationToken);
        await using var content = await source.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), cancellationToken);
        await destination.UploadAsync(content, new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            HttpHeaders = new BlobHttpHeaders { ContentType = properties.Value.ContentType },
        }, cancellationToken);
    }

    public async Task DeleteAsync(string container, string path, CancellationToken cancellationToken) =>
        await service.Value.GetBlobContainerClient(container).GetBlobClient(path)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);

    public async Task WriteAsync(string container, string path, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var containerClient = service.Value.GetBlobContainerClient(container);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await containerClient.GetBlobClient(path).UploadAsync(
            BinaryData.FromBytes(content), new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, cancellationToken);
    }

    private static string NameWithin(string fullName, string prefix) => fullName[prefix.Length..].TrimEnd('/');
}
