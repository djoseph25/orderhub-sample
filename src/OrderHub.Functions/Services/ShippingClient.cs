using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OrderHub.Contracts;
using OrderHub.Functions.Options;

namespace OrderHub.Functions.Services;

public sealed record ShipmentResult(string TrackingNumber);

/// <summary>The partner rejected the request (4xx). Retrying won't help, so dead-letter it.</summary>
public sealed class ShippingRejectedException(HttpStatusCode status, string? detail)
    : Exception($"Shipping partner rejected the request with {(int)status}: {detail}")
{
    public HttpStatusCode Status { get; } = status;
}

public interface IShippingClient
{
    Task<ShipmentResult> CreateShipmentAsync(OrderRecord order, CancellationToken ct);
}

/// <summary>
/// Week 3: a typed client. HttpClient is injected by IHttpClientFactory, which pools handlers
/// (avoiding socket exhaustion and stale DNS). Retries and timeouts come from the resilience handler, not from this class.
/// </summary>
public sealed class ShippingClient(HttpClient http) : IShippingClient
{
    public async Task<ShipmentResult> CreateShipmentAsync(OrderRecord order, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "shipments")
        {
            Content = JsonContent.Create(new
            {
                orderId = order.Id,
                postalCode = order.ShipToPostalCode,
                items = order.Lines,
            }),
        };
        // Lets the partner dedupe if we retry after a timeout where the first attempt actually succeeded.
        request.Headers.Add("Idempotency-Key", order.Id);

        using var response = await http.SendAsync(request, ct);

        if (IsPermanentFailure(response.StatusCode))
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new ShippingRejectedException(response.StatusCode, detail);
        }

        response.EnsureSuccessStatusCode(); // 5xx/408/429 left after retries -> HttpRequestException -> treated as transient

        var result = await response.Content.ReadFromJsonAsync<ShipmentResult>(ct);
        return result ?? throw new InvalidOperationException("Shipping partner returned an empty body.");
    }

    private static bool IsPermanentFailure(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
}

/// <summary>
/// Week 3/10: outbound auth as a DelegatingHandler, applied to every request on this client and nowhere else.
/// For OAuth2 client-credentials partners, fetch and cache a token here instead.
/// </summary>
public sealed class ApiKeyAuthHandler(IOptions<ShippingApiOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("x-api-key");
        request.Headers.Add("x-api-key", options.Value.ApiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
