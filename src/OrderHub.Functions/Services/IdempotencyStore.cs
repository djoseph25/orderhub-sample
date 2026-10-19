using Azure.Data.Tables;

namespace OrderHub.Functions.Services;

/// <summary>
/// Week 3: idempotent consumer. Service Bus is at-least-once, so the same message can arrive twice
/// (lock lost, host crash after the side effect but before Complete, a DLQ replay, and so on).
/// Record processed MessageIds and skip repeats. The downstream call also sends an Idempotency-Key,
/// which protects the gap between "side effect done" and "marked processed".
/// </summary>
public interface IIdempotencyStore
{
    Task<bool> HasProcessedAsync(string messageId, CancellationToken ct);
    Task MarkProcessedAsync(string messageId, CancellationToken ct);
}

public sealed class TableIdempotencyStore(TableServiceClient tables, TimeProvider time) : IIdempotencyStore
{
    public const string TableName = "processedmessages";
    private const string Partition = "msg";

    private readonly TableClient _table = tables.GetTableClient(TableName);

    public async Task<bool> HasProcessedAsync(string messageId, CancellationToken ct)
    {
        var existing = await _table.GetEntityIfExistsAsync<TableEntity>(Partition, messageId, select: ["RowKey"], cancellationToken: ct);
        return existing.HasValue;
    }

    // Add a cleanup job (or use Cosmos/Redis with TTL) so this table doesn't grow forever.
    public Task MarkProcessedAsync(string messageId, CancellationToken ct) =>
        _table.UpsertEntityAsync(new TableEntity(Partition, messageId) { ["ProcessedAt"] = time.GetUtcNow() }, cancellationToken: ct);
}
