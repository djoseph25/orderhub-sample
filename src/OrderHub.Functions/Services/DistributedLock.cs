using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

namespace OrderHub.Functions.Services;

/// <summary>
/// Week 3: a lock that works across instances.
/// A static SemaphoreSlim or lock() only protects one process. Once the Function app scales out to N instances,
/// N requests for the same record can run at the same time. A blob lease is an exclusive lock held in Azure Storage.
/// </summary>
public interface IDistributedLock
{
    /// <returns>A handle to dispose when done, or <c>null</c> if another instance holds the lock.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan leaseDuration, CancellationToken ct);
}

public sealed class BlobLeaseDistributedLock(BlobServiceClient blobs) : IDistributedLock
{
    public const string ContainerName = "locks";

    private readonly BlobContainerClient _container = blobs.GetBlobContainerClient(ContainerName);

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan leaseDuration, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(key);
        try
        {
            await blob.UploadAsync(BinaryData.Empty, overwrite: false, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Blob already exists, or is currently leased. Both are fine; the lease decides.
        }

        var lease = blob.GetBlobLeaseClient();
        try
        {
            // Leases must be 15-60 seconds (or infinite). For longer work, renew periodically.
            await lease.AcquireAsync(leaseDuration, cancellationToken: ct);
            return new Handle(lease);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return null;
        }
    }

    private sealed class Handle(BlobLeaseClient lease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await lease.ReleaseAsync();
            }
            catch (RequestFailedException)
            {
                // Lease already expired. That's acceptable, because the lease duration is the safety net.
            }
        }
    }
}
