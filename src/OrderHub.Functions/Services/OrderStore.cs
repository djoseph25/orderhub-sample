using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using OrderHub.Contracts;

namespace OrderHub.Functions.Services;

public sealed class OrderRecord
{
    public required string Id { get; init; }
    public required string CustomerId { get; init; }
    public required IReadOnlyList<OrderLine> Lines { get; init; }
    public string? ShipToPostalCode { get; init; }
    public OrderStatus Status { get; set; }
    public string? TrackingNumber { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Table storage ETag for optimistic concurrency. A stale write fails with 412 and doesn't overwrite.</summary>
    public ETag ETag { get; set; } = ETag.All;

    public OrderDto ToDto() => new(Id, CustomerId, Status, Lines, ShipToPostalCode, TrackingNumber, CreatedAt);
}

public interface IOrderStore
{
    Task<OrderRecord?> GetAsync(string orderId, CancellationToken ct);
    Task AddAsync(OrderRecord order, CancellationToken ct);

    /// <summary>Throws <see cref="RequestFailedException"/> with status 412 if someone else updated the order first.</summary>
    Task UpdateAsync(OrderRecord order, CancellationToken ct);

    IAsyncEnumerable<OrderRecord> GetSubmittedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct);
}

/// <summary>
/// Week 4/5: Azure.Data.Tables, all async, with no .Result/.GetAwaiter().GetResult().
/// A single partition keeps the sample simple. In a real system choose a PartitionKey that spreads load
/// (for example customer id) and matches your main query.
/// </summary>
public sealed class TableOrderStore(TableServiceClient tables) : IOrderStore
{
    public const string TableName = "orders";
    private const string Partition = "order";

    private readonly TableClient _table = tables.GetTableClient(TableName);

    public async Task<OrderRecord?> GetAsync(string orderId, CancellationToken ct)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(Partition, orderId, cancellationToken: ct);
        return response.HasValue ? FromEntity(response.Value!) : null;
    }

    public Task AddAsync(OrderRecord order, CancellationToken ct) =>
        _table.AddEntityAsync(ToEntity(order), ct);

    public async Task UpdateAsync(OrderRecord order, CancellationToken ct)
    {
        var response = await _table.UpdateEntityAsync(ToEntity(order), order.ETag, TableUpdateMode.Replace, ct);
        order.ETag = response.Headers.ETag ?? order.ETag;
    }

    public async IAsyncEnumerable<OrderRecord> GetSubmittedBeforeAsync(
        DateTimeOffset cutoff, [EnumeratorCancellation] CancellationToken ct)
    {
        var status = OrderStatus.Submitted.ToString();
        // CreateQueryFilter escapes values, the Table storage equivalent of a parameterized SQL query.
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and Status eq {status} and CreatedAt lt {cutoff}");
        var query = _table.QueryAsync<TableEntity>(filter, cancellationToken: ct);

        await foreach (var entity in query)
        {
            yield return FromEntity(entity);
        }
    }

    private static TableEntity ToEntity(OrderRecord o) => new(Partition, o.Id)
    {
        ["CustomerId"] = o.CustomerId,
        ["Status"] = o.Status.ToString(),
        ["Lines"] = JsonSerializer.Serialize(o.Lines),
        ["ShipToPostalCode"] = o.ShipToPostalCode,
        ["TrackingNumber"] = o.TrackingNumber,
        ["CreatedAt"] = o.CreatedAt,
    };

    private static OrderRecord FromEntity(TableEntity e) => new()
    {
        Id = e.RowKey,
        CustomerId = e.GetString("CustomerId"),
        Status = Enum.Parse<OrderStatus>(e.GetString("Status")),
        Lines = JsonSerializer.Deserialize<List<OrderLine>>(e.GetString("Lines")) ?? [],
        ShipToPostalCode = e.GetString("ShipToPostalCode"),
        TrackingNumber = e.GetString("TrackingNumber"),
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
        ETag = e.ETag,
    };
}
