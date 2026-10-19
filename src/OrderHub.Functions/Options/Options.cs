using System.ComponentModel.DataAnnotations;

namespace OrderHub.Functions.Options;

public sealed class OrderHubOptions
{
    public const string SectionName = "OrderHub";

    [Required]
    public string OrdersQueueName { get; set; } = "orders";

    /// <summary>Orders still Submitted after this long get flagged by the timer sweep.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "30.00:00:00")]
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// We dead-letter ourselves at this delivery count, so we can attach a meaningful reason.
    /// Keep it at or below the queue's maxDeliveryCount (see infra/main.bicep), which is the broker-side backstop.
    /// </summary>
    [Range(1, 50)]
    public int MaxDeliveryAttempts { get; set; } = 5;
}

public sealed class ShippingApiOptions
{
    public const string SectionName = "ShippingApi";

    [Required]
    public Uri BaseUrl { get; set; } = default!;

    /// <summary>In Azure this comes from a Key Vault reference app setting, never from source control (Week 5).</summary>
    [Required, MinLength(1)]
    public string ApiKey { get; set; } = default!;
}
