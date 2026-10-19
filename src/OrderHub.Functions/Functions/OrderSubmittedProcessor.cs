using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderHub.Contracts;
using OrderHub.Functions.Options;
using OrderHub.Functions.Services;

namespace OrderHub.Functions.Functions;

/// <summary>
/// Week 2, the core Service Bus pattern: session-enabled queue with explicit message settlement.
///
///   Outcome                                      Action
///   ------------------------------------------   ----------------------------------------------
///   Success                                      Complete
///   Already processed (duplicate)                Complete, no side effects
///   Bad payload / unknown order / 4xx (poison)   DeadLetter now with a reason; retrying won't help
///   Transient failure (5xx, timeout)             Abandon, Service Bus redelivers (DeliveryCount++)
///   Transient, but retries used up               DeadLetter "RetriesExhausted"
///   Host shutting down                           Abandon so another instance picks it up promptly
///
/// AutoCompleteMessages=false means nothing is settled unless we do it. A message we never settle is
/// redelivered when its lock expires.
/// </summary>
public sealed class OrderSubmittedProcessor(
    IOrderStore store,
    IIdempotencyStore idempotency,
    IShippingClient shipping,
    IOptions<OrderHubOptions> options,
    ILogger<OrderSubmittedProcessor> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function(nameof(OrderSubmittedProcessor))]
    public async Task RunAsync(
        [ServiceBusTrigger("%OrderHub:OrdersQueueName%", Connection = "ServiceBus", IsSessionsEnabled = true, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
    {
        OrderSubmittedMessage? body;
        try
        {
            body = message.Body.ToObjectFromJson<OrderSubmittedMessage>(Json);
        }
        catch (JsonException ex)
        {
            await DeadLetterAsync(message, actions, "InvalidPayload", ex.Message, ex);
            return;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.OrderId))
        {
            await DeadLetterAsync(message, actions, "InvalidPayload", "Body was empty or missing OrderId.", null);
            return;
        }

        try
        {
            if (await idempotency.HasProcessedAsync(message.MessageId, ct))
            {
                Log.DuplicateMessage(logger, message.MessageId);
                await actions.CompleteMessageAsync(message, ct);
                return;
            }

            var order = await store.GetAsync(body.OrderId, ct);
            if (order is null)
            {
                await DeadLetterAsync(message, actions, "OrderNotFound", $"Order {body.OrderId} does not exist.", null);
                return;
            }

            if (order.Status is OrderStatus.Submitted)
            {
                var shipment = await shipping.CreateShipmentAsync(order, ct);
                order.Status = OrderStatus.Shipped;
                order.TrackingNumber = shipment.TrackingNumber;
                await store.UpdateAsync(order, ct);
                Log.OrderShipped(logger, order.Id, shipment.TrackingNumber);
            }
            // else: cancelled or already shipped, nothing to do, but still a successful outcome.

            await idempotency.MarkProcessedAsync(message.MessageId, ct);
            await actions.CompleteMessageAsync(message, ct);
        }
        catch (ShippingRejectedException ex)
        {
            await DeadLetterAsync(message, actions, "ShippingRejected", ex.Message, ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.ShutdownAbandon(logger, message.MessageId);
            await actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       or TimeoutException
                                       or TaskCanceledException // HttpClient.Timeout, not host shutdown (handled above)
                                       or Polly.Timeout.TimeoutRejectedException
                                       or Polly.CircuitBreaker.BrokenCircuitException
                                       or Azure.RequestFailedException)
        {
            if (message.DeliveryCount >= options.Value.MaxDeliveryAttempts)
            {
                await DeadLetterAsync(message, actions, "RetriesExhausted", ex.Message, ex);
                return;
            }

            Log.Abandoning(logger, message.MessageId, message.DeliveryCount, ex);
            await actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
    }

    private async Task DeadLetterAsync(
        ServiceBusReceivedMessage message, ServiceBusMessageActions actions, string reason, string description, Exception? ex)
    {
        Log.DeadLettering(logger, message.MessageId, reason, ex);
        // Max 4096 chars for the description. Keep it short and NEVER put sensitive payload data in it.
        var trimmed = description.Length > 1024 ? description[..1024] : description;
        await actions.DeadLetterMessageAsync(message, deadLetterReason: reason, deadLetterErrorDescription: trimmed, cancellationToken: CancellationToken.None);
    }
}
