using System.Text.Json;
using Azure;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OrderHub.Contracts;
using OrderHub.Functions.Middleware;
using OrderHub.Functions.Services;

namespace OrderHub.Functions.Functions;

/// <summary>
/// HTTP routes (all under /api because of host.json routePrefix):
///   POST /api/v1/orders              create and publish to Service Bus
///   GET  /api/v1/orders/{id}         read
///   POST /api/v1/orders/{id}/cancel  cancel, guarded by a distributed lock
/// AuthorizationLevel.Function requires a function key (x-functions-key header or ?code=).
/// For user or partner auth, put APIM or Easy Auth in front, or validate a JWT in middleware (see the Api project for JWT).
/// </summary>
public sealed class OrdersHttpFunctions(
    IOrderStore store,
    IOrderPublisher publisher,
    IDistributedLock locks,
    IValidator<CreateOrderRequest> validator,
    TimeProvider time,
    ILogger<OrdersHttpFunctions> logger)
{
    [Function("CreateOrder")]
    public async Task<IActionResult> CreateAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/orders")] HttpRequest req,
        FunctionContext context,
        CancellationToken ct)
    {
        CreateOrderRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<CreateOrderRequest>(ct);
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "Request body is not valid JSON.");
        }

        if (body is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Request body is required.");
        }

        var validation = await validator.ValidateAsync(body, ct);
        if (!validation.IsValid)
        {
            return new BadRequestObjectResult(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var order = new OrderRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            CustomerId = body.CustomerId,
            Lines = body.Lines,
            ShipToPostalCode = body.ShipToPostalCode,
            Status = OrderStatus.Submitted,
            CreatedAt = time.GetUtcNow(),
        };

        await store.AddAsync(order, ct);
        await publisher.PublishSubmittedAsync(new OrderSubmittedMessage(order.Id, order.CustomerId, context.GetCorrelationId()), ct);

        Log.OrderCreated(logger, order.Id, order.CustomerId);
        return new CreatedResult($"/api/v1/orders/{order.Id}", order.ToDto());
    }

    [Function("GetOrder")]
    public async Task<IActionResult> GetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "v1/orders/{id}")] HttpRequest req,
        string id,
        CancellationToken ct)
    {
        var order = await store.GetAsync(id, ct);
        return order is null
            ? Problem(StatusCodes.Status404NotFound, $"Order '{id}' was not found.")
            : new OkObjectResult(order.ToDto());
    }

    [Function("CancelOrder")]
    public async Task<IActionResult> CancelAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/orders/{id}/cancel")] HttpRequest req,
        string id,
        CancellationToken ct)
    {
        // Two cancel requests for the same order on different instances can't interleave.
        await using var handle = await locks.TryAcquireAsync($"order-{id}", TimeSpan.FromSeconds(15), ct);
        if (handle is null)
        {
            return Problem(StatusCodes.Status409Conflict, "The order is being modified by another request. Retry shortly.");
        }

        var order = await store.GetAsync(id, ct);
        if (order is null)
        {
            return Problem(StatusCodes.Status404NotFound, $"Order '{id}' was not found.");
        }

        if (order.Status is not OrderStatus.Submitted)
        {
            return Problem(StatusCodes.Status409Conflict, $"Order is {order.Status} and can no longer be cancelled.");
        }

        order.Status = OrderStatus.Cancelled;
        try
        {
            await store.UpdateAsync(order, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status412PreconditionFailed)
        {
            // The Service Bus processor shipped it between our read and write. The ETag check caught it.
            return Problem(StatusCodes.Status409Conflict, "The order changed while cancelling. Reload and retry.");
        }

        Log.OrderCancelled(logger, order.Id);
        return new OkObjectResult(order.ToDto());
    }

    private static ObjectResult Problem(int status, string detail) =>
        new(new ProblemDetails { Status = status, Detail = detail }) { StatusCode = status };
}
