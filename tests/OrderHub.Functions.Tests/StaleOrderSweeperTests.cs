using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderHub.Contracts;
using OrderHub.Functions.Functions;
using OrderHub.Functions.Options;
using OrderHub.Functions.Services;
using static OrderHub.Functions.Tests.TestSupport;

namespace OrderHub.Functions.Tests;

[TestClass]
public sealed class StaleOrderSweeperTests
{
    [TestMethod]
    public async Task Flags_old_submitted_orders_and_skips_concurrently_changed_ones()
    {
        var store = new Mock<IOrderStore>();
        var stale = Order("old-1");
        var raced = Order("old-2");
        DateTimeOffset? cutoffUsed = null;

        store.Setup(s => s.GetSubmittedBeforeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((cutoff, _) => cutoffUsed = cutoff)
            .Returns(ToAsync(stale, raced));
        store.Setup(s => s.UpdateAsync(raced, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "Precondition failed"));

        var sut = new StaleOrderSweeper(
            store.Object,
            Microsoft.Extensions.Options.Options.Create(new OrderHubOptions { StaleAfter = TimeSpan.FromHours(6) }),
            new FixedTimeProvider(Now),
            NullLogger<StaleOrderSweeper>.Instance);

        await sut.RunAsync(new TimerInfo { IsPastDue = true }, CancellationToken.None);

        Assert.AreEqual(Now.AddHours(-6), cutoffUsed);
        Assert.AreEqual(OrderStatus.Stale, stale.Status);
        store.Verify(s => s.UpdateAsync(It.IsAny<OrderRecord>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
