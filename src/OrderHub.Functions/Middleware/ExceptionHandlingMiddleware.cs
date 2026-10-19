using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace OrderHub.Functions.Middleware;

/// <summary>
/// HTTP: unhandled exceptions become an RFC 9457 ProblemDetails 500 response, and stack traces never leak to callers.
/// Non-HTTP triggers rethrow on purpose. For Service Bus, a throw abandons the message and redelivery/DLQ semantics take over.
/// Swallowing exceptions there would silently lose messages.
/// </summary>
public sealed class ExceptionHandlingMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var logger = context.GetLogger<ExceptionHandlingMiddleware>();
            Log.UnhandledException(logger, ex, context.FunctionDefinition.Name);

            var httpContext = context.GetHttpContext();
            if (httpContext is null || httpContext.Response.HasStarted)
            {
                throw;
            }

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Extensions = { ["correlationId"] = context.GetCorrelationId() },
            });
        }
    }
}
