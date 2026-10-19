using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderHub.Functions.Options;

namespace OrderHub.Functions.Functions;

/// <summary>
/// Week 2: dead-letter queue operations.
///   GET  /api/ops/dlq?max=20         peek (read without removing)
///   POST /api/ops/dlq/replay?max=10  move messages back to the main queue
/// AuthorizationLevel.Admin requires the host master key, so only operators can call these.
/// (The route can't start with "admin"; the Functions host reserves that prefix.)
/// </summary>
public sealed class DeadLetterOpsFunctions(ServiceBusClient client, IOptions<OrderHubOptions> options, ILogger<DeadLetterOpsFunctions> logger)
{
    [Function("PeekDeadLetters")]
    public async Task<IActionResult> PeekAsync(
        [HttpTrigger(AuthorizationLevel.Admin, "get", Route = "ops/dlq")] HttpRequest req,
        CancellationToken ct)
    {
        await using var receiver = CreateDeadLetterReceiver();
        var messages = await receiver.PeekMessagesAsync(ReadMax(req, 20), cancellationToken: ct);

        // Metadata only. Don't return message bodies from an ops endpoint; they may contain sensitive data.
        return new OkObjectResult(messages.Select(m => new
        {
            m.MessageId,
            m.SessionId,
            m.DeadLetterReason,
            m.DeadLetterErrorDescription,
            m.DeliveryCount,
            m.EnqueuedTime,
        }));
    }

    [Function("ReplayDeadLetters")]
    public async Task<IActionResult> ReplayAsync(
        [HttpTrigger(AuthorizationLevel.Admin, "post", Route = "ops/dlq/replay")] HttpRequest req,
        CancellationToken ct)
    {
        await using var receiver = CreateDeadLetterReceiver();
        await using var sender = client.CreateSender(options.Value.OrdersQueueName);

        var batch = await receiver.ReceiveMessagesAsync(ReadMax(req, 10), TimeSpan.FromSeconds(5), ct);
        foreach (var dead in batch)
        {
            // The copy constructor keeps body, MessageId, SessionId, CorrelationId and application properties.
            var retry = new ServiceBusMessage(dead);
            retry.ApplicationProperties["ReplayedFromDlq"] = true;

            await sender.SendMessageAsync(retry, ct);
            await receiver.CompleteMessageAsync(dead, ct);
        }

        Log.DeadLettersReplayed(logger, batch.Count);
        return new OkObjectResult(new { replayed = batch.Count });
    }

    private ServiceBusReceiver CreateDeadLetterReceiver() =>
        // The DLQ of a session-enabled queue is not session-enabled, so a plain receiver works.
        client.CreateReceiver(options.Value.OrdersQueueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    private static int ReadMax(HttpRequest req, int fallback) =>
        int.TryParse(req.Query["max"], out var max) ? Math.Clamp(max, 1, 100) : fallback;
}
