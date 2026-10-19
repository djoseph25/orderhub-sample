using System.Net;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderHub.Contracts;
using OrderHub.Functions.Functions;
using OrderHub.Functions.Options;
using OrderHub.Functions.Services;
using static OrderHub.Functions.Tests.TestSupport;

namespace OrderHub.Functions.Tests;

/// <summary>
/// Week 7: unit-testing a Service Bus function. ServiceBusModelFactory builds a received message
/// (its constructor is internal), and ServiceBusMessageActions is mockable, so every settlement path can be checked.
/// </summary>
[TestClass]
public sealed class OrderSubmittedProcessorTests
{
    private readonly Mock<IOrderStore> _store = new();
    private readonly Mock<IIdempotencyStore> _idempotency = new();
    private readonly Mock<IShippingClient> _shipping = new();
    private readonly Mock<ServiceBusMessageActions> _actions = new();

    private OrderSubmittedProcessor CreateSut(int maxDeliveryAttempts = 5) => new(
        _store.Object,
        _idempotency.Object,
        _shipping.Object,
        Microsoft.Extensions.Options.Options.Create(new OrderHubOptions { MaxDeliveryAttempts = maxDeliveryAttempts }),
        NullLogger<OrderSubmittedProcessor>.Instance);

    private static ServiceBusReceivedMessage Message(string? json = null, int deliveryCount = 1) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(json ?? """{"orderId":"order-1","customerId":"customer-1","correlationId":"corr-1"}"""),
            messageId: "order-1",
            sessionId: "customer-1",
            deliveryCount: deliveryCount);

    [TestMethod]
    public async Task Ships_order_and_completes_message()
    {
        var order = Order();
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(order);
        _shipping.Setup(s => s.CreateShipmentAsync(order, It.IsAny<CancellationToken>())).ReturnsAsync(new ShipmentResult("TRACK-1"));
        var message = Message();

        await CreateSut().RunAsync(message, _actions.Object, CancellationToken.None);

        Assert.AreEqual(OrderStatus.Shipped, order.Status);
        Assert.AreEqual("TRACK-1", order.TrackingNumber);
        _store.Verify(s => s.UpdateAsync(order, It.IsAny<CancellationToken>()), Times.Once);
        _idempotency.Verify(i => i.MarkProcessedAsync("order-1", It.IsAny<CancellationToken>()), Times.Once);
        _actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Duplicate_message_is_completed_without_side_effects()
    {
        _idempotency.Setup(i => i.HasProcessedAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var message = Message();

        await CreateSut().RunAsync(message, _actions.Object, CancellationToken.None);

        _shipping.VerifyNoOtherCalls();
        _store.VerifyNoOtherCalls();
        _actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Cancelled_order_is_completed_without_shipping()
    {
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(Order(status: OrderStatus.Cancelled));
        var message = Message();

        await CreateSut().RunAsync(message, _actions.Object, CancellationToken.None);

        _shipping.VerifyNoOtherCalls();
        _actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("not json at all", "InvalidPayload")]
    [DataRow("""{"customerId":"c"}""", "InvalidPayload")]
    public async Task Poison_payload_is_dead_lettered(string json, string expectedReason)
    {
        await CreateSut().RunAsync(Message(json), _actions.Object, CancellationToken.None);

        VerifyDeadLettered(expectedReason);
        _actions.Verify(a => a.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Unknown_order_is_dead_lettered()
    {
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync((OrderRecord?)null);

        await CreateSut().RunAsync(Message(), _actions.Object, CancellationToken.None);

        VerifyDeadLettered("OrderNotFound");
    }

    [TestMethod]
    public async Task Permanent_partner_rejection_is_dead_lettered_immediately()
    {
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(Order());
        _shipping.Setup(s => s.CreateShipmentAsync(It.IsAny<OrderRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ShippingRejectedException(HttpStatusCode.BadRequest, "bad postal code"));

        await CreateSut().RunAsync(Message(deliveryCount: 1), _actions.Object, CancellationToken.None);

        VerifyDeadLettered("ShippingRejected");
        _actions.Verify(a => a.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Transient_failure_is_abandoned_for_redelivery()
    {
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(Order());
        _shipping.Setup(s => s.CreateShipmentAsync(It.IsAny<OrderRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503"));
        var message = Message(deliveryCount: 2);

        await CreateSut(maxDeliveryAttempts: 5).RunAsync(message, _actions.Object, CancellationToken.None);

        _actions.Verify(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
        _idempotency.Verify(i => i.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Transient_failure_on_last_attempt_is_dead_lettered()
    {
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(Order());
        _shipping.Setup(s => s.CreateShipmentAsync(It.IsAny<OrderRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503"));

        await CreateSut(maxDeliveryAttempts: 5).RunAsync(Message(deliveryCount: 5), _actions.Object, CancellationToken.None);

        VerifyDeadLettered("RetriesExhausted");
    }

    private void VerifyDeadLettered(string reason) =>
        _actions.Verify(a => a.DeadLetterMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<Dictionary<string, object>>(),
            reason,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
}
