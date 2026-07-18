using System.Diagnostics.CodeAnalysis;
using Harness.Host.Workers;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.Workers;

[SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible",
    Justification = "The same watchdog scenario must execute unchanged against both persistence providers.")]
internal static class DurableExecutionWatchdogBehavior
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string StaleExecutionId = "01ARZ3NDEKTSV4RRFFQ69G5FB9";
    private const string TimerExecutionId = "01ARZ3NDEKTSV4RRFFQ69G5FBA";

    public static async Task AssertAsync(
        IDurableExecutionEngine engine,
        CancellationToken cancellationToken)
    {
        var initial = new DateTimeOffset(2026, 7, 18, 16, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(initial.AddSeconds(10));

        await engine.StartAsync(
            StartRequest(StaleExecutionId, "watchdog:start:stale", initial, maxAttempts: 2),
            initial,
            cancellationToken);
        var firstLease = await engine.TryAcquireNextAsync(
            TenantId,
            "watchdog-owner-1",
            initial,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        Assert.NotNull(firstLease);
        var checkpoint = await engine.CheckpointAsync(
            new DurableCheckpointCommand(
                TenantId,
                StaleExecutionId,
                firstLease.AttemptId,
                firstLease.Owner,
                firstLease.FencingToken,
                "commit-watchdog",
                "{\"commit\":\"abc123\"}",
                initial.AddSeconds(1),
                "watchdog:checkpoint"),
            cancellationToken);
        Assert.Equal(DurableCommandStatus.Applied, checkpoint.Status);

        await engine.StartAsync(
            StartRequest(TimerExecutionId, "watchdog:start:timer", initial, maxAttempts: 2),
            initial,
            cancellationToken);
        var timerLease = await engine.TryAcquireNextAsync(
            TenantId,
            "watchdog-timer-owner",
            initial,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.NotNull(timerLease);
        var scheduled = await engine.ScheduleTimerAsync(
            new DurableTimerCommand(
                TenantId,
                TimerExecutionId,
                "signal-timeout",
                initial.AddSeconds(5),
                "{}",
                initial.AddSeconds(1),
                "watchdog:timer"),
            cancellationToken);
        Assert.Equal(DurableExecutionState.WaitingForSignal, scheduled.State);

        var concurrentCycles = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ =>
                CreateWorker(engine, clock).RunOnceAsync(cancellationToken)));
        Assert.Equal(1, concurrentCycles.Sum(item => item.TimersFired));
        Assert.Equal(1, concurrentCycles.Sum(item => item.Requeued));
        Assert.Equal(0, concurrentCycles.Sum(item => item.DeadLettered));
        Assert.Equal(
            [StaleExecutionId],
            concurrentCycles.SelectMany(item => item.ExecutionIds).Distinct(StringComparer.Ordinal));

        var waitingRetry = await engine.GetAsync(TenantId, StaleExecutionId, cancellationToken);
        var timerReady = await engine.GetAsync(TenantId, TimerExecutionId, cancellationToken);
        Assert.NotNull(waitingRetry);
        Assert.NotNull(timerReady);
        Assert.Equal(DurableExecutionState.WaitingForRetry, waitingRetry.State);
        Assert.Equal("commit-watchdog", waitingRetry.LatestCheckpointKey);
        Assert.Equal(DurableExecutionState.Ready, timerReady.State);
        var cancelledTimerExecution = await engine.CancelAsync(
            TenantId,
            TimerExecutionId,
            timerReady.Version,
            clock.UtcNow,
            "watchdog:timer:cancel",
            cancellationToken);
        Assert.Equal(DurableExecutionState.Cancelled, cancelledTimerExecution.State);

        var staleWrite = await engine.RecordHeartbeatAsync(
            new DurableLeaseCommand(
                TenantId,
                StaleExecutionId,
                firstLease.AttemptId,
                firstLease.Owner,
                firstLease.FencingToken,
                clock.UtcNow,
                "watchdog:stale-heartbeat"),
            cancellationToken);
        Assert.Equal(DurableCommandStatus.LeaseRejected, staleWrite.Status);

        var restartedWorker = CreateWorker(engine, clock);
        var replayCycle = await restartedWorker.RunOnceAsync(cancellationToken);
        var afterReplay = await engine.GetAsync(TenantId, StaleExecutionId, cancellationToken);
        Assert.Equal(0, replayCycle.TimersFired);
        Assert.Equal(0, replayCycle.Requeued);
        Assert.Equal(0, replayCycle.DeadLettered);
        Assert.NotNull(afterReplay);
        Assert.Equal(waitingRetry.Version, afterReplay.Version);
        Assert.Equal(waitingRetry.LatestCheckpointKey, afterReplay.LatestCheckpointKey);

        clock.UtcNow = initial.AddSeconds(11);
        var retryCycle = await restartedWorker.RunOnceAsync(cancellationToken);
        Assert.Equal(1, retryCycle.TimersFired);
        var secondLease = await engine.TryAcquireNextAsync(
            TenantId,
            "watchdog-owner-2",
            clock.UtcNow,
            TimeSpan.FromSeconds(5),
            cancellationToken);
        Assert.NotNull(secondLease);
        Assert.Equal(2, secondLease.AttemptNumber);
        Assert.True(secondLease.FencingToken > firstLease.FencingToken);
        Assert.Equal("commit-watchdog", secondLease.LatestCheckpointKey);

        clock.UtcNow = initial.AddSeconds(20);
        var terminalCycle = await restartedWorker.RunOnceAsync(cancellationToken);
        Assert.Equal(0, terminalCycle.Requeued);
        Assert.Equal(1, terminalCycle.DeadLettered);
        Assert.Equal([StaleExecutionId], terminalCycle.ExecutionIds);
        var deadLetter = await engine.GetAsync(TenantId, StaleExecutionId, cancellationToken);
        Assert.NotNull(deadLetter);
        Assert.Equal(DurableExecutionState.DeadLetter, deadLetter.State);
        Assert.Null(deadLetter.ActiveAttemptId);

        var terminalReplay = await CreateWorker(engine, clock).RunOnceAsync(cancellationToken);
        var unchanged = await engine.GetAsync(TenantId, StaleExecutionId, cancellationToken);
        Assert.Equal(0, terminalReplay.DeadLettered);
        Assert.NotNull(unchanged);
        Assert.Equal(deadLetter.Version, unchanged.Version);

        await restartedWorker.StartAsync(cancellationToken);
        await restartedWorker.StopAsync(cancellationToken);
    }

    private static DurableExecutionWatchdogBackgroundService CreateWorker(
        IDurableExecutionEngine engine,
        IClock clock) =>
        new(
            engine,
            clock,
            new DurableExecutionWatchdogOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(2)),
            NullLogger<DurableExecutionWatchdogBackgroundService>.Instance);

    private static DurableExecutionStartRequest StartRequest(
        string executionId,
        string idempotencyKey,
        DateTimeOffset availableAt,
        int maxAttempts) =>
        new(
            TenantId,
            ProjectId,
            executionId,
            "{\"job\":\"watchdog\"}",
            new DurableRetryPolicy(
                maxAttempts,
                TimeSpan.FromSeconds(1),
                2m,
                TimeSpan.FromSeconds(30)),
            availableAt,
            idempotencyKey);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
