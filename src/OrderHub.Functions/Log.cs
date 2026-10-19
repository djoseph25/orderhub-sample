using Microsoft.Extensions.Logging;

namespace OrderHub.Functions;

/// <summary>
/// Week 6: source-generated, structured logging.
/// Don't use logger.LogInformation($"Order {id}"). Interpolation renders the string up front, so you lose the queryable
/// OrderId field in App Insights (customDimensions.OrderId) and pay the formatting cost even when the level is disabled.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Order {OrderId} created for customer {CustomerId}")]
    public static partial void OrderCreated(ILogger logger, string orderId, string customerId);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Order {OrderId} cancelled")]
    public static partial void OrderCancelled(ILogger logger, string orderId);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Order {OrderId} shipped with tracking {TrackingNumber}")]
    public static partial void OrderShipped(ILogger logger, string orderId, string trackingNumber);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Message {MessageId} already processed, completing without side effects")]
    public static partial void DuplicateMessage(ILogger logger, string messageId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Dead-lettering message {MessageId}: {Reason}")]
    public static partial void DeadLettering(ILogger logger, string messageId, string reason, Exception? exception);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Transient failure on message {MessageId} (delivery {DeliveryCount}), abandoning for redelivery")]
    public static partial void Abandoning(ILogger logger, string messageId, int deliveryCount, Exception exception);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Host shutting down, abandoning message {MessageId}")]
    public static partial void ShutdownAbandon(ILogger logger, string messageId);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning, Message = "Timer is past due; schedule was missed (host down or scaled to zero)")]
    public static partial void TimerPastDue(ILogger logger);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Stale sweep flagged {Count} orders")]
    public static partial void StaleSweepCompleted(ILogger logger, int count);

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Replayed {Count} dead-lettered messages")]
    public static partial void DeadLettersReplayed(ILogger logger, int count);

    [LoggerMessage(EventId = 9000, Level = LogLevel.Error, Message = "Unhandled exception in function {FunctionName}")]
    public static partial void UnhandledException(ILogger logger, Exception exception, string functionName);
}
