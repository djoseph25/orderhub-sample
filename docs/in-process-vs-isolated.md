# Week 1 cheat sheet: in-process to isolated worker

The in-process model loses support on **November 10, 2026**. This is the side-by-side to keep open while migrating.

## Project file

```xml
<!-- IN-PROCESS (before) -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Sdk.Functions" Version="4.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Extensions" Version="1.1.0" />
    <PackageReference Include="Microsoft.Azure.WebJobs.Extensions.ServiceBus" Version="5.*" />
  </ItemGroup>
</Project>

<!-- ISOLATED (after), see src/OrderHub.Functions/OrderHub.Functions.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>                       <!-- it's now its own process -->
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.ServiceBus" />
  </ItemGroup>
</Project>
```

## Package mapping

| In-process (WebJobs) | Isolated (Worker) |
|---|---|
| `Microsoft.NET.Sdk.Functions` | `Microsoft.Azure.Functions.Worker` + `.Worker.Sdk` |
| `Microsoft.Azure.Functions.Extensions` (FunctionsStartup) | not needed, use `Program.cs` |
| `Microsoft.Azure.WebJobs.Extensions.ServiceBus` | `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` |
| `Microsoft.Azure.WebJobs.Extensions.Storage` | `...Worker.Extensions.Storage.Blobs` / `.Queues` / `.Tables` |
| built-in Timer | `...Worker.Extensions.Timer` |
| HTTP (built-in) | `...Worker.Extensions.Http.AspNetCore` |
| `Microsoft.Azure.WebJobs.Logging.ApplicationInsights` | `Microsoft.Azure.Functions.Worker.ApplicationInsights` + `Microsoft.ApplicationInsights.WorkerService` |

## Startup

```csharp
// IN-PROCESS
[assembly: FunctionsStartup(typeof(MyApp.Startup))]
public class Startup : FunctionsStartup
{
    public override void Configure(IFunctionsHostBuilder builder)
    {
        var config = builder.GetContext().Configuration;
        builder.Services.AddSingleton<IFoo, Foo>();
    }
}

// ISOLATED, see src/OrderHub.Functions/Program.cs
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.UseMiddleware<CorrelationIdMiddleware>();       // new: real middleware pipeline
builder.Services.AddApplicationInsightsTelemetryWorkerService().ConfigureFunctionsApplicationInsights();
builder.Services.AddSingleton<IFoo, Foo>();
builder.Build().Run();
```
You don't build `IConfigurationRoot` by hand from local.settings.json. `builder.Configuration` already has app settings, env vars and user secrets.

## Function signatures

```csharp
// IN-PROCESS
[FunctionName("CreateOrder")]
public async Task<IActionResult> Run(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/orders")] HttpRequest req,
    ILogger log)                                       // ILogger injected per call

// ISOLATED
[Function("CreateOrder")]                              // [FunctionName] becomes [Function]
public async Task<IActionResult> CreateAsync(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/orders")] HttpRequest req,
    FunctionContext context,                           // invocation metadata
    CancellationToken ct)                              // honor it
// ILogger<T> comes via constructor injection, so functions are instance classes now.
```

## Service Bus

```csharp
// IN-PROCESS
[FunctionName("Processor")]
public async Task Run(
    [ServiceBusTrigger("orders", Connection = "ServiceBus", IsSessionsEnabled = true)] ServiceBusReceivedMessage msg,
    ServiceBusMessageActions actions)                  // Microsoft.Azure.WebJobs.ServiceBus

// ISOLATED, see Functions/OrderSubmittedProcessor.cs
[Function("Processor")]
public async Task RunAsync(
    [ServiceBusTrigger("orders", Connection = "ServiceBus", IsSessionsEnabled = true, AutoCompleteMessages = false)]
    ServiceBusReceivedMessage msg,
    ServiceBusMessageActions actions,                  // Microsoft.Azure.Functions.Worker
    CancellationToken ct)
```

## Output bindings
In-process `IAsyncCollector<T>` / `[ServiceBus("queue")] out` has no direct equivalent. Use either
a return-value output binding (`[ServiceBusOutput("queue")]` on the return type), or, as this sample does, an injected
`ServiceBusClient`/`ServiceBusSender`. The injected client gives full control over SessionId, MessageId and batching.

## Settings
| Setting | In-process | Isolated |
|---|---|---|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet` | `dotnet-isolated` |
| Azure app config | .NET 6 | .NET 8 / 10 stack: Linux `linuxFxVersion` = `DOTNET-ISOLATED\|10.0`, or Flex `functionAppConfig.runtime` |

## Migration checklist
1. Branch. Re-save any UTF-16 files as UTF-8.
2. Swap packages per the table, add `OutputType=Exe`, and set the TFM to net8.0 or net10.0.
3. Replace `Startup` with `Program.cs`. Move DI registrations over unchanged.
4. Rename `[FunctionName]` to `[Function]`, make classes non-static, and constructor-inject `ILogger<T>`.
5. Replace output bindings / `IAsyncCollector` with injected SDK clients.
6. Fix `using` namespaces (`Microsoft.Azure.WebJobs` becomes `Microsoft.Azure.Functions.Worker`).
7. Set `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` locally and in every environment.
8. Re-check App Insights. Worker logs need the filter-rule removal shown in Program.cs.
9. Run the tests, then `func start`, then hit every route (Postman collection), and check that Service Bus messages settle.
10. Deploy to a non-prod slot or environment first, then compare App Insights failures, durations and dependencies.
