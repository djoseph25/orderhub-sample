using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderHub.Contracts;
using OrderHub.Functions.Options;
using OrderHub.Functions.Services;

namespace OrderHub.Functions.Functions;

/// <summary>
/// Week 4: timer trigger.
/// - Timer functions are singletons: the host holds a blob lease, so only one instance runs each tick even when scaled out.
/// - The schedule is an NCRONTAB expression with 6 fields (seconds first): "0 */15 * * * *" means every 15 minutes.
/// - IsPastDue is true when a tick was missed. The handler should be safe to run late or twice.
/// - Never set RunOnStartup=true in production. It fires on every scale-out and restart.
/// </summary>
public sealed class StaleOrderSweeper(
    IOrderStore store,
    IOptions<OrderHubOptions> options,
    TimeProvider time,
    ILogger<StaleOrderSweeper> logger)
{
    [Function(nameof(StaleOrderSweeper))]
    public async Task RunAsync([TimerTrigger("%OrderHub:StaleSweepSchedule%")] TimerInfo timer, CancellationToken ct)
    {
        if (timer.IsPastDue)
        {
            Log.TimerPastDue(logger);
        }

        var cutoff = time.GetUtcNow() - options.Value.StaleAfter;
        var count = 0;

        await foreach (var order in store.GetSubmittedBeforeAsync(cutoff, ct))
        {
            order.Status = OrderStatus.Stale;
            try
            {
                await store.UpdateAsync(order, ct);
                count++;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Changed since we read it (shipped or cancelled). Leave it alone.
            }
        }

        Log.StaleSweepCompleted(logger, count);
    }
}
