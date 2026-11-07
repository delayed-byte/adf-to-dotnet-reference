using System.Globalization;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.KeyStone.Card;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Master;

/// <summary>
/// The evidence sink of PL_Master_NightlyBatch (LS_Blob_Storage). Content streams into staged blocks of a block blob, and the
/// block list (with the metadata and content type) is committed only after the writer completes: until then the blob does not
/// change, so a failed attempt leaves neither a partial file nor a damaged earlier one. <c>BlockBlobClient.OpenWriteAsync</c> is
/// not used because its stream commits whatever was written when it is disposed, also after a failure. The SDK's own retries
/// are off (<see cref="AzureCardBlobStore.CreateClientOptions"/>, ADR 0010); the orchestrator's RetryPolicy is the only layer.
/// The connection string holds the account key and is never logged.
/// </summary>
public sealed class AzureEvidenceBlobStore(IOptions<BlobStorageOptions> options) : IEvidenceBlobStore
{
    /// <summary>Size of one staged block. The evidence of a day is a few kilobytes, so it is normally one block.</summary>
    public const int BlockSize = 4 * 1024 * 1024;

    private readonly Lazy<BlobServiceClient> service = new(() => new BlobServiceClient(options.Value.ConnectionString, AzureCardBlobStore.CreateClientOptions()));

    public async Task WriteAsync(string container, string blobName, IReadOnlyDictionary<string, string> metadata, string contentType,
        Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(writeContent);
        var containerClient = service.Value.GetBlobContainerClient(container);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = containerClient.GetBlockBlobClient(blobName);

        await using var staging = new StagedBlockStream(blob, BlockSize);
        await writeContent(staging, cancellationToken);
        await staging.CommitAsync(new CommitBlockListOptions
        {
            Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        }, cancellationToken);
    }

    /// <summary>A write-only stream that stages a block every <c>blockSize</c> bytes and commits nothing until <see cref="CommitAsync"/>.</summary>
    private sealed class StagedBlockStream(BlockBlobClient blob, int blockSize) : Stream
    {
        private readonly MemoryStream buffer = new();
        private readonly List<string> blockIds = [];
        private long length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => length;

        public override long Position
        {
            get => length;
            set => throw new NotSupportedException();
        }

        public async Task CommitAsync(CommitBlockListOptions commitOptions, CancellationToken cancellationToken)
        {
            if (buffer.Length > 0)
            {
                await StageAsync(cancellationToken);
            }

            await blob.CommitBlockListAsync(blockIds, commitOptions, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            this.buffer.Write(buffer);
            length += buffer.Length;
            if (this.buffer.Length >= blockSize)
            {
                var id = NextBlockId();
                this.buffer.Position = 0;
                blob.StageBlock(id, this.buffer, new BlockBlobStageBlockOptions());
                Staged(id);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            this.buffer.Write(buffer.Span);
            length += buffer.Length;
            if (this.buffer.Length >= blockSize)
            {
                await StageAsync(cancellationToken);
            }
        }

        // Staging happens at block boundaries and on commit; a flush has nothing to do.
        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                buffer.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task StageAsync(CancellationToken cancellationToken)
        {
            var id = NextBlockId();
            buffer.Position = 0;
            await blob.StageBlockAsync(id, buffer, new BlockBlobStageBlockOptions(), cancellationToken);
            Staged(id);
        }

        // Block IDs must have the same length within a blob; staging an ID again replaces the uncommitted block.
        private string NextBlockId() => Convert.ToBase64String(Encoding.ASCII.GetBytes(blockIds.Count.ToString("D6", CultureInfo.InvariantCulture)));

        private void Staged(string id)
        {
            blockIds.Add(id);
            buffer.SetLength(0);
        }
    }
}
