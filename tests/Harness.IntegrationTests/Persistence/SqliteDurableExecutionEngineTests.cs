using System.Diagnostics.CodeAnalysis;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Persistence;

[SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible",
    Justification = "The scenario is intentionally written against the provider-neutral engine contract for PostgreSQL reuse.")]
public sealed class SqliteDurableExecutionEngineTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    [Fact]
    public async Task LifecycleIsIdempotentFencedRetryableAndRecoverable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-durable-sqlite",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "durable.db"),
                timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await SeedFoundationAsync(dispatcher, timeout.Token);
            var engine = new SqliteDurableExecutionEngine(dispatcher);
            var initial = new DateTimeOffset(2026, 7, 18, 14, 20, 0, TimeSpan.Zero);
            var request = StartRequest("01ARZ3NDEKTSV4RRFFQ69G5FB6", "start:primary", initial, maxAttempts: 3);

            var started = await engine.StartAsync(request, initial, timeout.Token);
            var replay = await engine.StartAsync(request, initial, timeout.Token);
            var conflict = await engine.StartAsync(
                request with { PayloadJson = "{\"job\":\"different\"}" },
                initial,
                timeout.Token);

            Assert.Equal(DurableCommandStatus.Applied, started.Status);
            Assert.Equal(DurableCommandStatus.IdempotentReplay, replay.Status);
            Assert.Equal(DurableCommandStatus.IdempotencyConflict, conflict.Status);

            var acquisitions = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
                engine.TryAcquireNextAsync(
                    TenantId,
                    "owner-primary",
                    initial,
                    TimeSpan.FromSeconds(30),
                    timeout.Token)));
            var lease = Assert.Single(acquisitions, acquisition => acquisition is not null)!;
            Assert.Equal(1, lease.AttemptNumber);
            Assert.Equal(1, lease.FencingToken);

            var heartbeat = LeaseCommand(lease, initial.AddSeconds(1), "heartbeat:1");
            var heartbeatResult = await engine.RecordHeartbeatAsync(heartbeat, timeout.Token);
            var renewed = await engine.RenewLeaseAsync(
                LeaseCommand(lease, initial.AddSeconds(2), "renew:1"),
                TimeSpan.FromSeconds(60),
                timeout.Token);
            var checkpoint = new DurableCheckpointCommand(
                TenantId,
                lease.ExecutionId,
                lease.AttemptId,
                lease.Owner,
                lease.FencingToken,
                "commit-1",
                "{\"commit\":\"abc123\"}",
                initial.AddSeconds(3),
                "checkpoint:1");
            var checkpointResult = await engine.CheckpointAsync(checkpoint, timeout.Token);
            var checkpointReplay = await engine.CheckpointAsync(checkpoint, timeout.Token);
            var checkpointKeyReplay = await engine.CheckpointAsync(
                checkpoint with { IdempotencyKey = "checkpoint:equivalent" },
                timeout.Token);
            var checkpointConflict = await engine.CheckpointAsync(
                checkpoint with
                {
                    IdempotencyKey = "checkpoint:conflict",
                    PayloadJson = "{\"commit\":\"different\"}",
                },
                timeout.Token);

            Assert.Equal(DurableCommandStatus.Applied, heartbeatResult.Status);
            Assert.Equal(DurableCommandStatus.Applied, renewed.Status);
            Assert.Equal(DurableCommandStatus.Applied, checkpointResult.Status);
            Assert.Equal(DurableCommandStatus.IdempotentReplay, checkpointReplay.Status);
            Assert.Equal(DurableCommandStatus.IdempotentReplay, checkpointKeyReplay.Status);
            Assert.Equal(DurableCommandStatus.IdempotencyConflict, checkpointConflict.Status);

            var completed = await engine.CompleteAsync(
                LeaseCommand(lease, initial.AddSeconds(4), "complete:1"),
                timeout.Token);
            var staleHeartbeat = await engine.RecordHeartbeatAsync(
                LeaseCommand(lease, initial.AddSeconds(5), "heartbeat:stale"),
                timeout.Token);
            var completedSnapshot = await engine.GetAsync(TenantId, lease.ExecutionId, timeout.Token);

            Assert.Equal(DurableExecutionState.Completed, completed.State);
            Assert.Equal(DurableCommandStatus.LeaseRejected, staleHeartbeat.Status);
            Assert.NotNull(completedSnapshot);
            Assert.Equal(DurableExecutionState.Completed, completedSnapshot.State);
            Assert.Null(completedSnapshot.ActiveAttemptId);
            Assert.Equal("commit-1", completedSnapshot.LatestCheckpointKey);
            Assert.Equal("{\"commit\":\"abc123\"}", completedSnapshot.LatestCheckpointJson);

            await AssertRetryAndReconciliationAsync(engine, initial.AddMinutes(1), timeout.Token);
            await AssertTimerSignalAndLifecycleAsync(engine, initial.AddMinutes(2), timeout.Token);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static async Task AssertRetryAndReconciliationAsync(
        IDurableExecutionEngine engine,
        DateTimeOffset initial,
        CancellationToken cancellationToken)
    {
        var executionId = "01ARZ3NDEKTSV4RRFFQ69G5FB7";
        await engine.StartAsync(StartRequest(executionId, "start:retry", initial, maxAttempts: 2), initial, cancellationToken);
        var firstLease = await engine.TryAcquireNextAsync(
            TenantId,
            "owner-retry-1",
            initial,
            TimeSpan.FromSeconds(10),
            cancellationToken);
        Assert.NotNull(firstLease);

        var failed = await engine.FailAsync(
            new DurableFailureCommand(
                TenantId,
                executionId,
                firstLease.AttemptId,
                firstLease.Owner,
                firstLease.FencingToken,
                "transient",
                "Synthetic transient failure.",
                initial.AddSeconds(1),
                "fail:retry-1"),
            cancellationToken);
        Assert.Equal(DurableExecutionState.WaitingForRetry, failed.State);
        Assert.Null(await engine.TryAcquireNextAsync(
            TenantId,
            "owner-too-early",
            initial.AddSeconds(1),
            TimeSpan.FromSeconds(10),
            cancellationToken));

        Assert.Equal(1, await engine.FireDueTimersAsync(TenantId, initial.AddSeconds(2), cancellationToken));
        var secondLease = await engine.TryAcquireNextAsync(
            TenantId,
            "owner-retry-2",
            initial.AddSeconds(2),
            TimeSpan.FromSeconds(10),
            cancellationToken);
        Assert.NotNull(secondLease);
        Assert.Equal(2, secondLease.AttemptNumber);
        Assert.True(secondLease.FencingToken > firstLease.FencingToken);

        var reconciliation = await engine.ReconcileAsync(
            TenantId,
            heartbeatStaleBefore: initial.AddSeconds(20),
            now: initial.AddSeconds(20),
            cancellationToken);
        Assert.Equal(0, reconciliation.Requeued);
        Assert.Equal(1, reconciliation.DeadLettered);
        Assert.Equal([executionId], reconciliation.ExecutionIds);
        var snapshot = await engine.GetAsync(TenantId, executionId, cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DurableExecutionState.DeadLetter, snapshot.State);
        Assert.Null(snapshot.ActiveAttemptId);
    }

    private static async Task AssertTimerSignalAndLifecycleAsync(
        IDurableExecutionEngine engine,
        DateTimeOffset initial,
        CancellationToken cancellationToken)
    {
        var executionId = "01ARZ3NDEKTSV4RRFFQ69G5FB8";
        await engine.StartAsync(StartRequest(executionId, "start:timer", initial, maxAttempts: 2), initial, cancellationToken);
        var lease = await engine.TryAcquireNextAsync(
            TenantId,
            "owner-timer",
            initial,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.NotNull(lease);
        var scheduled = await engine.ScheduleTimerAsync(
            new DurableTimerCommand(
                TenantId,
                executionId,
                "wake-up",
                initial.AddSeconds(10),
                "{}",
                initial.AddSeconds(1),
                "timer:schedule"),
            cancellationToken);
        Assert.Equal(DurableExecutionState.WaitingForSignal, scheduled.State);

        var signaled = await engine.SignalAsync(
            new DurableSignalCommand(
                TenantId,
                executionId,
                "manual-wake",
                "{}",
                initial.AddSeconds(2),
                "signal:wake"),
            cancellationToken);
        Assert.Equal(DurableExecutionState.Ready, signaled.State);

        var snapshot = await engine.GetAsync(TenantId, executionId, cancellationToken);
        Assert.NotNull(snapshot);
        var paused = await engine.PauseAsync(
            TenantId,
            executionId,
            snapshot.Version,
            initial.AddSeconds(3),
            "lifecycle:pause",
            cancellationToken);
        var resumed = await engine.ResumeAsync(
            TenantId,
            executionId,
            paused.Version!.Value,
            initial.AddSeconds(4),
            "lifecycle:resume",
            cancellationToken);
        var cancelled = await engine.CancelAsync(
            TenantId,
            executionId,
            resumed.Version!.Value,
            initial.AddSeconds(5),
            "lifecycle:cancel",
            cancellationToken);
        Assert.Equal(DurableExecutionState.Paused, paused.State);
        Assert.Equal(DurableExecutionState.Ready, resumed.State);
        Assert.Equal(DurableExecutionState.Cancelled, cancelled.State);
    }

    private static DurableExecutionStartRequest StartRequest(
        string executionId,
        string idempotencyKey,
        DateTimeOffset availableAt,
        int maxAttempts) => new(
            TenantId,
            ProjectId,
            executionId,
            "{\"job\":\"test\"}",
            new DurableRetryPolicy(
                maxAttempts,
                TimeSpan.FromSeconds(1),
                2m,
                TimeSpan.FromSeconds(30)),
            availableAt,
            idempotencyKey);

    private static DurableLeaseCommand LeaseCommand(
        DurableExecutionLease lease,
        DateTimeOffset occurredAt,
        string idempotencyKey) => new(
            lease.TenantId,
            lease.ExecutionId,
            lease.AttemptId,
            lease.Owner,
            lease.FencingToken,
            occurredAt,
            idempotencyKey);

    private static async Task SeedFoundationAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var command = new ProjectProvisionCommand(
            TenantId,
            "Tenant",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "Organization",
            ProjectId,
            "Project",
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            "Local User",
            "durable:foundation",
            new string('D', 64),
            "01ARZ3NDEKTSV4RRFFQ69G5FAZ",
            "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            "project.created",
            "{\"projectId\":\"01ARZ3NDEKTSV4RRFFQ69G5FAX\"}",
            new DateTimeOffset(2026, 7, 18, 14, 19, 0, TimeSpan.Zero));
        await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(command, cancellationToken);
    }
}
