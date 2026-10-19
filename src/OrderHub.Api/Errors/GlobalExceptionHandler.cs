using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderHub.Api.Errors;

/// <summary>
/// Week 12: central exception-to-HTTP mapping (IExceptionHandler, .NET 8+).
/// Controllers stay free of try/catch for cross-cutting failures.
/// </summary>
public sealed partial class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The resource was modified by someone else. Reload and retry."),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Client closed request."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        if (status >= 500)
        {
            LogUnhandled(logger, exception);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
