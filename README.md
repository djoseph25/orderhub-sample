# OrderHub: Azure Functions and Web API study sample

A small, fully generic sample (no company code, names or data) that puts every concept from the 15-week study plan
into one runnable solution. All tests pass, and every pattern has comments explaining *why*.

```
OrderHubSample/
├─ src/
│  ├─ OrderHub.Contracts/     shared DTOs and message contracts ("SDK" package pattern)
│  ├─ OrderHub.Functions/     Azure Functions, .NET 10 isolated worker: HTTP + Service Bus + Timer
│  └─ OrderHub.Api/           ASP.NET Core Web API: Auth0 JWT, versioning, EF Core, ProblemDetails
├─ tests/
│  ├─ OrderHub.Functions.Tests/   MSTest + Moq unit tests (21)
│  └─ OrderHub.Api.Tests/         WebApplicationFactory integration tests (10)
├─ infra/main.bicep           Function app (Flex Consumption), Storage, Service Bus, Key Vault, App Insights, RBAC
├─ pipelines/                 Azure DevOps multi-stage YAML and step template
├─ postman/                   collection and environment for every route
├─ emulators/                 docker-compose for Azurite and the Service Bus emulator
├─ docs/in-process-vs-isolated.md   Week 1 migration cheat sheet
├─ Directory.Build.props      shared build settings
└─ Directory.Packages.props   Central Package Management (all versions in one place)
```

## How it flows

```
POST /api/v1/orders ─▶ validate (FluentValidation) ─▶ save (Table storage) ─▶ publish (Service Bus, SessionId = customer)
                                                                                     │
                     OrderSubmittedProcessor ◀───────────────────────────────────────┘
                       ├─ duplicate?           ─▶ Complete
                       ├─ bad payload / 4xx    ─▶ DeadLetter (with reason)
                       ├─ 5xx / timeout        ─▶ Abandon ─▶ redelivered ─▶ DeadLetter after N attempts
                       └─ ok: call shipping API (typed HttpClient + resilience) ─▶ mark Shipped ─▶ Complete

StaleOrderSweeper (timer, every 15 min) ─▶ flags orders stuck in Submitted
GET/POST /api/ops/dlq[/replay]           ─▶ inspect and replay dead letters (master key)
```

## Routes

| Project | Method and route | Auth |
|---|---|---|
| Functions | `POST /api/v1/orders` | function key |
| Functions | `GET  /api/v1/orders/{id}` | function key |
| Functions | `POST /api/v1/orders/{id}/cancel` | function key |
| Functions | `GET  /api/ops/dlq?max=20` | master key |
| Functions | `POST /api/ops/dlq/replay?max=10` | master key |
| API | `GET /api/v1/products?page&pageSize` | `read:products` |
| API | `GET /api/v1/products/{id}` | `read:products` |
| API | `POST /api/v1/products` | `write:products` |
| API | `PUT /api/v1/products/{id}` (optimistic concurrency) | `write:products` |
| API | `DELETE /api/v1/products/{id}` | `write:products` |
| API | `GET /api/v2/products` (new response shape) | `read:products` |
| API | `GET /health` | anonymous |

## Study map: week to where to look

| Week | Concept | Files |
|---|---|---|
| 1 | Isolated worker, Program.cs, middleware | `Functions/Program.cs`, `Middleware/*`, `docs/in-process-vs-isolated.md` |
| 2 | Service Bus sessions, manual settlement, DLQ, host.json tuning | `Functions/OrderSubmittedProcessor.cs`, `Functions/DeadLetterOpsFunctions.cs`, `Services/OrderPublisher.cs`, `host.json` |
| 3 | IHttpClientFactory, resilience, DelegatingHandler, idempotency, distributed lock | `Extensions/ServiceCollectionExtensions.cs`, `Services/ShippingClient.cs`, `Services/IdempotencyStore.cs`, `Services/DistributedLock.cs` |
| 4 | Async everywhere, CancellationToken, timer triggers, ETag concurrency | `Services/OrderStore.cs`, `Services/StorageInitializer.cs`, `Functions/StaleOrderSweeper.cs` |
| 5 | Managed Identity, DefaultAzureCredential, Key Vault references, options validation | `Extensions/ServiceCollectionExtensions.cs`, `Options/Options.cs`, `infra/main.bicep` |
| 6 | App Insights (worker), correlation ids, `[LoggerMessage]` | `Program.cs`, `Middleware/CorrelationIdMiddleware.cs`, `Log.cs` |
| 7 | Testing functions: mock `ServiceBusMessageActions`, `ServiceBusModelFactory`, `DefaultHttpContext`, `TimeProvider` | `tests/OrderHub.Functions.Tests/*` |
| 8 | Bicep, pipeline templates, Central Package Management, transitive CVE pinning | `infra/`, `pipelines/`, `Directory.Packages.props` |
| 9 | Hosting, DI, middleware order, "platform package" style extension methods | `Api/Program.cs`, `Functions/Extensions/ServiceCollectionExtensions.cs` |
| 10 | Auth0 JWT, scope policies, `IAuthorizationHandler` | `Api/Auth/ScopeAuthorization.cs`, `Api/Program.cs` |
| 11 | EF Core: migrations, AsNoTracking, projection, concurrency token, ExecuteDelete, migration bundles | `Api/Data/*`, `Api/Controllers/V1/ProductsController.cs`, `pipelines/azure-pipelines.yml` |
| 12 | Asp.Versioning, OpenAPI per version, ProblemDetails, `IExceptionHandler`, FluentValidation | `Api/Program.cs`, `Api/Controllers/V2/*`, `Api/Errors/*`, `Api/Validation/*` |
| 13 | `Azure.Messaging.ServiceBus` (the modern SDK), typed clients | `Services/OrderPublisher.cs`, `Services/ShippingClient.cs` |
| 14 | `WebApplicationFactory`, SQLite swap, fake auth scheme | `tests/OrderHub.Api.Tests/*` |
| 15 | Modernization | `docs/in-process-vs-isolated.md`. Use the .NET Upgrade Assistant on a copy of a legacy project. |

## Run it

**Prerequisites:** .NET 10 SDK, Azure Functions Core Tools v4, and Azurite. The Service Bus emulator (Docker) or a dev Service Bus namespace is optional.

```bash
dotnet test OrderHub.slnx                 # 31 tests, no Azure needed
```

**Functions locally**
```bash
docker compose -f emulators/docker-compose.yml up -d     # or run Azurite on its own (no Docker)
cp src/OrderHub.Functions/local.settings.sample.json src/OrderHub.Functions/local.settings.json
cd src/OrderHub.Functions && func start
```
No Docker? Run `azurite`, set `"ServiceBus"` in local.settings.json to a dev namespace connection string,
and create a **session-enabled** queue called `orders`. Better still, use `ServiceBus__fullyQualifiedNamespace` and `az login`
so DefaultAzureCredential uses your identity. You'll need the *Azure Service Bus Data Owner* role on the namespace.

**API locally** (uses SQL LocalDB and applies migrations automatically in Development)
```bash
dotnet run --project src/OrderHub.Api --launch-profile https
```
For real tokens, create an Auth0 API (audience `https://orderhub-catalog-api`) with `read:products` and `write:products`
permissions, plus a machine-to-machine app. Put those values in `appsettings.json` and the Postman environment.

**Postman CLI**
```bash
postman collection run postman/OrderHub.postman_collection.json -e postman/local.postman_environment.json
```

**Deploy to your own subscription**
```bash
az group create -n rg-orderhub-dev -l eastus2
az deployment group create -g rg-orderhub-dev -f infra/main.bicep -p shippingApiBaseUrl=https://example.com/ shippingApiKey=dummy
```

## Exercises (break it on purpose)

1. **Week 2:** Make the shipping URL point nowhere. Watch DeliveryCount climb, then the message land in the DLQ with `RetriesExhausted`. Replay it with `/api/ops/dlq/replay`.
2. **Week 2:** Add a second function that listens to the same queue. What happens to message distribution? (Competing consumers.)
3. **Week 3:** Replace `BlobLeaseDistributedLock` with a `static SemaphoreSlim` and explain why it breaks at 2+ instances.
4. **Week 4:** Add `.GetAwaiter().GetResult()` somewhere and load-test with many concurrent requests. Watch thread-pool starvation.
5. **Week 6:** Write KQL for: failures by function, p95 duration, dead-letter events (`traces | where customDimensions.EventId == 2002`), and an end-to-end trace by `customDimensions.CorrelationId`.
6. **Week 7:** Add a test for the shutdown path (`OperationCanceledException` when the token is cancelled, which should Abandon).
7. **Week 11:** Add a `Category` table with a relationship. Create a migration and look at the generated SQL (`dotnet ef migrations script`).
8. **Week 12:** Mark v1 deprecated (`[ApiVersion(1.0, Deprecated = true)]`) and check the `api-deprecated-versions` header.
9. **Week 14:** Swap SQLite for Testcontainers SQL Server and see which tests behave differently.

## Copying this to another machine

Everything here is generic, so it's safe to put in a personal repo:
```bash
git init && git add . && git commit -m "OrderHub study sample"
git remote add origin https://github.com/<you>/orderhub-sample.git && git push -u origin main
```
`local.settings.json`, `bin/` and `obj/` are git-ignored. Only the `.sample.json` template is committed.
