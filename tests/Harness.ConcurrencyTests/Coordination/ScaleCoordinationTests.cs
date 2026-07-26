using Harness.Modules.Coordination.Application;
using Harness.Modules.Execution.Application.Git;
using Harness.Modules.Execution.Domain.Git;

namespace Harness.ConcurrencyTests.Coordination;

public sealed class ScaleCoordinationTests
{
    [Fact]
    public async Task TwentyFourDisjointCardsFanOutWithoutScopeConflictsAndFanInAfterBarrier()
    {
        const int workerCount = 24;
        var workers = Enumerable.Range(0, workerCount)
            .Select(index => new CardDependencyNode(
                $"worker-{index:D2}",
                [$"artifact-{index:D2}"],
                []))
            .ToArray();
        var integration = new CardDependencyNode(
            "integration",
            ["release-candidate"],
            workers.SelectMany(worker => worker.Provides).ToArray());
        var plan = CardDependencyGraph.Build([.. workers, integration]);

        Assert.True(plan.IsValid);
        Assert.Equal(workerCount, plan.DispatchWaves[0].Count);
        var barrier = Assert.Single(plan.FanInBarriers);
        Assert.Equal(workerCount, barrier.ProviderCardIds.Count);

        var claims = new ScopeClaimRegistry();
        var acquisitions = await Task.WhenAll(workers.Select((worker, index) =>
            Task.Run(() => claims.TryAcquire(
                worker.CardId,
                [new ScopeClaim($"src/feature-{index:D2}/**")]))));
        Assert.All(acquisitions, acquisition => Assert.True(acquisition.Acquired));

        var buffer = new CardPrioritizedBuffer();
        await Task.WhenAll(workers.Select((worker, index) =>
            Task.Run(() => buffer.Enqueue(
                worker.CardId,
                "worker",
                CardPriority.Normal,
                DateTimeOffset.UnixEpoch.AddMilliseconds(index)))));
        var dispatched = new ScaleDispatcher().Dispatch(buffer, workerCount, 0);
        Assert.Equal(workerCount, dispatched.DispatchedWorkerCards.Count);
        Assert.Equal(
            workerCount,
            dispatched.DispatchedWorkerCards.Distinct(StringComparer.Ordinal).Count());
        var beforeBarrier = plan.GetReadyCards(
            dispatched.DispatchedWorkerCards.Take(workerCount - 1).ToHashSet(StringComparer.Ordinal));
        Assert.Single(beforeBarrier);
        Assert.StartsWith("worker-", beforeBarrier[0], StringComparison.Ordinal);
        Assert.Equal(
            ["integration"],
            plan.GetReadyCards(dispatched.DispatchedWorkerCards.ToHashSet(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task MergeCoordinatorSerializesTwentyFourSubmissionsAndMeasuresContention()
    {
        const int mergeCount = 24;
        using var coordinator = new SerializedMergeCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;

        var first = coordinator.ExecuteAsync(
            "card-00",
            async token =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return "card-00";
            });
        await firstEntered.Task;

        var remaining = Enumerable.Range(1, mergeCount - 1)
            .Select(index => coordinator.ExecuteAsync(
                $"card-{index:D2}",
                _ =>
                {
                    var current = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximumActive, current);
                    Interlocked.Decrement(ref active);
                    return Task.FromResult($"card-{index:D2}");
                }))
            .ToArray();

        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Snapshot().Waiting == mergeCount - 1,
            TimeSpan.FromSeconds(5)));
        releaseFirst.SetResult();
        var results = await Task.WhenAll([first, .. remaining]);

        Assert.Equal(mergeCount, results.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, maximumActive);
        var snapshot = coordinator.Snapshot();
        Assert.Equal(mergeCount, snapshot.Enqueued);
        Assert.Equal(mergeCount, snapshot.Serialized);
        Assert.Equal(mergeCount - 1, snapshot.Contended);
        Assert.Equal(0, snapshot.Active);
        Assert.Equal(0, snapshot.Waiting);
        Assert.True(snapshot.MaximumWait > TimeSpan.Zero);
        Assert.True(snapshot.ShouldEvaluateValkey(serverMode: true));
        Assert.False(snapshot.ShouldEvaluateValkey(serverMode: false));
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
