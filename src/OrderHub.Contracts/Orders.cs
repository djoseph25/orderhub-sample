namespace OrderHub.Contracts;

public enum OrderStatus
{
    Submitted,
    Shipped,
    Cancelled,
    Stale,
}

public sealed record OrderLine(string Sku, int Quantity);

/// <summary>Body of <c>POST /api/v1/orders</c>.</summary>
public sealed record CreateOrderRequest(string CustomerId, IReadOnlyList<OrderLine> Lines, string? ShipToPostalCode);

public sealed record OrderDto(
    string Id,
    string CustomerId,
    OrderStatus Status,
    IReadOnlyList<OrderLine> Lines,
    string? ShipToPostalCode,
    string? TrackingNumber,
    DateTimeOffset CreatedAt);

/// <summary>
/// Message published to the <c>orders</c> Service Bus queue.
/// SessionId is the customer id, so each customer's orders are processed in order (Week 2).
/// MessageId is the order id, which feeds duplicate detection and idempotency (Week 3).
/// </summary>
public sealed record OrderSubmittedMessage(string OrderId, string CustomerId, string CorrelationId);
