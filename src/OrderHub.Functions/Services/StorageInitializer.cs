using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;

namespace OrderHub.Functions.Services;

/// <summary>
/// Creates tables and containers once at startup, asynchronously, instead of blocking inside a lock on the first request.
/// In Azure, the Bicep template creates these resources; this hosted service mainly helps local dev against Azurite.
/// </summary>
public sealed class StorageInitializer(TableServiceClient tables, BlobServiceClient blobs) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await tables.CreateTableIfNotExistsAsync(TableOrderStore.TableName, cancellationToken);
        await tables.CreateTableIfNotExistsAsync(TableIdempotencyStore.TableName, cancellationToken);
        await blobs.GetBlobContainerClient(BlobLeaseDistributedLock.ContainerName).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
