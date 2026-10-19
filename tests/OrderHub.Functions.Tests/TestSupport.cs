using Microsoft.Azure.Functions.Worker;
using Moq;
using OrderHub.Contracts;
using OrderHub.Functions.Services;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace OrderHub.Functions.Tests;

internal static class TestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public static OrderRecord Order(string id = "order-1", OrderStatus status = OrderStatus.Submitted, DateTimeOffset? createdAt = null) => new()
    {
        Id = id,
        CustomerId = "customer-1",
        Lines = [new OrderLine("SKU-1", 2)],
        ShipToPostalCode = "12345",
        Status = status,
        CreatedAt = createdAt ?? Now,
    };

    /// <summary>FunctionContext is abstract, so mock only what the code under test touches.</summary>
    public static FunctionContext FunctionContext(string correlationId = "corr-123")
    {
        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.InvocationId).Returns("invocation-1");
        context.SetupProperty(c => c.Items, new Dictionary<object, object> { ["CorrelationId"] = correlationId });
        return context.Object;
    }

    public static async IAsyncEnumerable<T> ToAsync<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }
}

/// <summary>A deterministic clock. Inject TimeProvider instead of calling DateTime.UtcNow so time-based logic is testable.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
