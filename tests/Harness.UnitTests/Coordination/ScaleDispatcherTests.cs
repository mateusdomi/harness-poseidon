using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class ScaleDispatcherTests
{
    [Fact]
    public void ScaleDispatcherDispatchesHighPriorityCardsFirstWithinLimit()
    {
        var buffer = new CardPrioritizedBuffer();
        var now = DateTimeOffset.UtcNow;

        buffer.Enqueue("card-low", "worker", CardPriority.Low, now);
        buffer.Enqueue("card-high", "worker", CardPriority.High, now);
        buffer.Enqueue("card-critic", "critic", CardPriority.Normal, now);

        var dispatcher = new ScaleDispatcher();
        var result = dispatcher.Dispatch(buffer, globalMaxConcurrency: 2, currentRunningCount: 0);

        Assert.Equal(2, result.DispatchedWorkerCards.Count + result.DispatchedCriticCards.Count);
        Assert.Contains("card-high", result.DispatchedWorkerCards);
        Assert.Contains("card-critic", result.DispatchedCriticCards);
        Assert.Equal(1, result.DeferredCount);
        Assert.Equal("dispatched_successfully", result.DispatchReason);
    }

    [Fact]
    public void ScaleDispatcherDefersAllWhenGlobalConcurrencyIsFull()
    {
        var buffer = new CardPrioritizedBuffer();
        buffer.Enqueue("card-1", "worker", CardPriority.High, DateTimeOffset.UtcNow);

        var dispatcher = new ScaleDispatcher();
        var result = dispatcher.Dispatch(buffer, globalMaxConcurrency: 5, currentRunningCount: 5);

        Assert.Empty(result.DispatchedWorkerCards);
        Assert.Equal(1, result.DeferredCount);
        Assert.Equal("global_concurrency_limit_reached", result.DispatchReason);
    }
}
