using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderHub.Functions.Extensions;
using OrderHub.Functions.Middleware;

// Week 1: the isolated worker host. This replaces the in-process FunctionsStartup.Configure(...)
// with a normal .NET generic host, so you own Program.cs, DI, configuration and middleware.
var builder = FunctionsApplication.CreateBuilder(args);

// ASP.NET Core integration: HTTP triggers get the real HttpRequest/IActionResult types.
builder.ConfigureFunctionsWebApplication();

// Middleware runs in registration order, outermost first (Week 1/6).
builder.UseMiddleware<CorrelationIdMiddleware>();
builder.UseMiddleware<ExceptionHandlingMiddleware>();

// Week 6: Application Insights for the worker process. The host process logs requests separately.
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// By default the AI provider only keeps Warning+ from the worker. Remove that rule so host.json/logLevel applies.
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(r =>
        r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

// Week 9: the app's own wiring lives in an extension method, the same pattern as an in-house platform package.
builder.Services.AddOrderHub(builder.Configuration);

builder.Build().Run();
