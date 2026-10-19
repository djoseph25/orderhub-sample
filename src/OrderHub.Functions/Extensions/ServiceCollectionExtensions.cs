using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OrderHub.Functions.Options;
using OrderHub.Functions.Services;

namespace OrderHub.Functions.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);

        // Week 3/5: strongly typed options with validation. The app fails at startup, not on the first request.
        services.AddOptions<OrderHubOptions>()
            .BindConfiguration(OrderHubOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ShippingApiOptions>()
            .BindConfiguration(ShippingApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAzureClients(configuration);

        services.AddSingleton<IOrderStore, TableOrderStore>();
        services.AddSingleton<IIdempotencyStore, TableIdempotencyStore>();
        services.AddSingleton<IDistributedLock, BlobLeaseDistributedLock>();
        services.AddSingleton<IOrderPublisher, ServiceBusOrderPublisher>();
        services.AddHostedService<StorageInitializer>();

        services.AddValidatorsFromAssemblyContaining<CreateOrderRequestValidator>();

        services.AddShippingClient();

        return services;
    }

    /// <summary>
    /// Week 5: one code path that works both locally (connection strings / emulators) and in Azure (Managed Identity).
    /// In Azure, set <c>ServiceBus__fullyQualifiedNamespace</c> and <c>OrderStorage__accountName</c> and leave out connection strings.
    /// The Service Bus trigger reads the same "ServiceBus" setting, so trigger and SDK client stay in sync.
    /// </summary>
    private static void AddAzureClients(this IServiceCollection services, IConfiguration configuration)
    {
        // In production you can swap in ManagedIdentityCredential for faster, more predictable token acquisition.
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddSingleton(sp =>
        {
            var ns = configuration["ServiceBus:fullyQualifiedNamespace"];
            return string.IsNullOrWhiteSpace(ns)
                ? new ServiceBusClient(configuration["ServiceBus"])
                : new ServiceBusClient(ns, sp.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton(sp =>
        {
            var account = configuration["OrderStorage:accountName"];
            return string.IsNullOrWhiteSpace(account)
                ? new TableServiceClient(configuration["OrderStorage"])
                : new TableServiceClient(new Uri($"https://{account}.table.core.windows.net"), sp.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton(sp =>
        {
            var account = configuration["OrderStorage:accountName"];
            return string.IsNullOrWhiteSpace(account)
                ? new BlobServiceClient(configuration["OrderStorage"])
                : new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), sp.GetRequiredService<TokenCredential>());
        });
    }

    /// <summary>
    /// Week 3: typed HttpClient from IHttpClientFactory, plus standard resilience (retry, circuit breaker, timeouts) and a DelegatingHandler for auth.
    /// Never call <c>new HttpClient()</c> per request or per service, and never change DefaultRequestHeaders on a shared client.
    /// </summary>
    private static void AddShippingClient(this IServiceCollection services)
    {
        services.AddTransient<ApiKeyAuthHandler>();

        var http = services.AddHttpClient<IShippingClient, ShippingClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ShippingApiOptions>>().Value;
            client.BaseAddress = options.BaseUrl;
        });

        // Handlers added first sit on the outside. Resilience goes outermost so every retry attempt
        // passes back through the auth handler, which matters if that handler refreshes tokens.
        http.AddStandardResilienceHandler(o =>
        {
            o.Retry.MaxRetryAttempts = 3;
            o.Retry.UseJitter = true;
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });
        http.AddHttpMessageHandler<ApiKeyAuthHandler>();
    }
}
