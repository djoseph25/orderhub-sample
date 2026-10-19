using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderHub.Contracts;
using OrderHub.Functions.Functions;
using OrderHub.Functions.Services;
using static OrderHub.Functions.Tests.TestSupport;

namespace OrderHub.Functions.Tests;

/// <summary>
/// With ASP.NET Core integration, HTTP functions take a real HttpRequest, so DefaultHttpContext is all you need. No host required.
/// </summary>
[TestClass]
public sealed class OrdersHttpFunctionsTests
{
    private readonly Mock<IOrderStore> _store = new();
    private readonly Mock<IOrderPublisher> _publisher = new();
    private readonly Mock<IDistributedLock> _locks = new();

    private OrdersHttpFunctions CreateSut() => new(
        _store.Object,
        _publisher.Object,
        _locks.Object,
        new CreateOrderRequestValidator(),
        new FixedTimeProvider(Now),
        NullLogger<OrdersHttpFunctions>.Instance);

    private static HttpRequest JsonRequest(object body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
        return context.Request;
    }

    [TestMethod]
    public async Task Create_valid_order_returns_201_and_publishes_with_correlation_id()
    {
        var request = new CreateOrderRequest("customer-1", [new OrderLine("SKU-1", 1)], "12345");

        var result = await CreateSut().CreateAsync(JsonRequest(request), FunctionContext("corr-abc"), CancellationToken.None);

        var created = Assert.IsInstanceOfType<CreatedResult>(result);
        var dto = Assert.IsInstanceOfType<OrderDto>(created.Value);
        Assert.AreEqual(OrderStatus.Submitted, dto.Status);
        Assert.AreEqual(Now, dto.CreatedAt);
        _store.Verify(s => s.AddAsync(It.IsAny<OrderRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _publisher.Verify(p => p.PublishSubmittedAsync(
            It.Is<OrderSubmittedMessage>(m => m.OrderId == dto.Id && m.CustomerId == "customer-1" && m.CorrelationId == "corr-abc"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Create_invalid_order_returns_400_validation_problem_and_publishes_nothing()
    {
        var request = new CreateOrderRequest("", [], "not-a-zip");

        var result = await CreateSut().CreateAsync(JsonRequest(request), FunctionContext(), CancellationToken.None);

        var badRequest = Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var problem = Assert.IsInstanceOfType<ValidationProblemDetails>(badRequest.Value);
        Assert.IsTrue(problem.Errors.ContainsKey("CustomerId"));
        Assert.IsTrue(problem.Errors.ContainsKey("Lines"));
        Assert.IsTrue(problem.Errors.ContainsKey("ShipToPostalCode"));
        _publisher.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Get_missing_order_returns_404()
    {
        var result = await CreateSut().GetAsync(new DefaultHttpContext().Request, "nope", CancellationToken.None);

        Assert.AreEqual(404, Assert.IsInstanceOfType<ObjectResult>(result).StatusCode);
    }

    [TestMethod]
    public async Task Cancel_returns_409_when_another_instance_holds_the_lock()
    {
        _locks.Setup(l => l.TryAcquireAsync("order-order-1", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var result = await CreateSut().CancelAsync(new DefaultHttpContext().Request, "order-1", CancellationToken.None);

        Assert.AreEqual(409, Assert.IsInstanceOfType<ObjectResult>(result).StatusCode);
        _store.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Cancel_shipped_order_returns_409()
    {
        _locks.Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(Order(status: OrderStatus.Shipped));

        var result = await CreateSut().CancelAsync(new DefaultHttpContext().Request, "order-1", CancellationToken.None);

        Assert.AreEqual(409, Assert.IsInstanceOfType<ObjectResult>(result).StatusCode);
        _store.Verify(s => s.UpdateAsync(It.IsAny<OrderRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Cancel_submitted_order_succeeds_and_releases_lock()
    {
        var handle = new Mock<IAsyncDisposable>();
        _locks.Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var order = Order();
        _store.Setup(s => s.GetAsync("order-1", It.IsAny<CancellationToken>())).ReturnsAsync(order);

        var result = await CreateSut().CancelAsync(new DefaultHttpContext().Request, "order-1", CancellationToken.None);

        Assert.IsInstanceOfType<OkObjectResult>(result);
        Assert.AreEqual(OrderStatus.Cancelled, order.Status);
        handle.Verify(h => h.DisposeAsync(), Times.Once);
    }
}
