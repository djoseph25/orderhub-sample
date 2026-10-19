using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using OrderHub.Contracts;
using OrderHub.Functions.Options;

namespace OrderHub.Functions.Services;

public interface IOrderPublisher
{
    Task PublishSubmittedAsync(OrderSubmittedMessage message, CancellationToken ct);
}

/// <summary>
/// Week 2: publishing with Azure.Messaging.ServiceBus (the current SDK; Microsoft.Azure.ServiceBus is deprecated).
/// ServiceBusClient and ServiceBusSender are thread-safe and expensive to create, so register them as singletons.
/// </summary>
public sealed class ServiceBusOrderPublisher(ServiceBusClient client, IOptions<OrderHubOptions> options) : IOrderPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(options.Value.OrdersQueueName);

    public Task PublishSubmittedAsync(OrderSubmittedMessage message, CancellationToken ct)
    {
        var sbMessage = new ServiceBusMessage(BinaryData.FromObjectAsJson(message))
        {
            ContentType = "application/json",
            Subject = nameof(OrderSubmittedMessage),
            // Duplicate detection on the queue drops a second send with the same MessageId inside the detection window.
            MessageId = message.OrderId,
            // Session = per-customer FIFO. One instance processes a session at a time.
            SessionId = message.CustomerId,
            CorrelationId = message.CorrelationId,
        };

        // Note: we save the order to Table storage and then publish. If the publish fails, the order exists
        // without a message (the "dual write" problem). The stale-order timer catches this case.
        // The robust fix is the transactional outbox pattern.
        return _sender.SendMessageAsync(sbMessage, ct);
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}
