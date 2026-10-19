using System.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace OrderHub.Functions.Middleware;

/// <summary>
/// Weeks 1 and 6: one correlation id per unit of work, whatever the trigger type.
/// HTTP reads the x-correlation-id header, Service Bus reads the message's CorrelationId, and anything else falls back to the invocation id.
/// The id goes into a logging scope, so every log line in the invocation carries it as a custom dimension.
/// </summary>
public sealed class CorrelationIdMiddleware : IFunctionsWorkerMiddleware
{
    public const string HeaderName = "x-correlation-id";
    public const string ItemKey = "CorrelationId";

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();

        string? correlationId = httpContext?.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId)
            && context.BindingContext.BindingData.TryGetValue("CorrelationId", out var fromMessage))
        {
            correlationId = fromMessage?.ToString();
        }

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = context.InvocationId;
        }

        context.Items[ItemKey] = correlationId;
        Activity.Current?.SetTag(ItemKey, correlationId);

        if (httpContext is not null)
        {
            httpContext.Response.OnStarting(() =>
            {
                httpContext.Response.Headers[HeaderName] = correlationId;
                return Task.CompletedTask;
            });
        }

        var logger = context.GetLogger<CorrelationIdMiddleware>();
        using (logger.BeginScope(new Dictionary<string, object> { [ItemKey] = correlationId }))
        {
            await next(context);
        }
    }
}

public static class FunctionContextExtensions
{
    public static string GetCorrelationId(this FunctionContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var value) && value is string id
            ? id
            : context.InvocationId;
}
