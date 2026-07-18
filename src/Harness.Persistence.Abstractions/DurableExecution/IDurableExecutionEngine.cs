namespace Harness.Persistence.Abstractions.DurableExecution;

public interface IDurableExecutionEngine
{
    Task<IReadOnlyList<string>> ListMaintenanceTenantsAsync(
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> StartAsync(
        DurableExecutionStartRequest request,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> PauseAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> ResumeAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> CancelAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<DurableExecutionLease?> TryAcquireNextAsync(
        string tenantId,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> RenewLeaseAsync(
        DurableLeaseCommand command,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> RecordHeartbeatAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> CheckpointAsync(
        DurableCheckpointCommand command,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> CompleteAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> FailAsync(
        DurableFailureCommand command,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> SignalAsync(
        DurableSignalCommand command,
        CancellationToken cancellationToken = default);

    Task<DurableCommandResult> ScheduleTimerAsync(
        DurableTimerCommand command,
        CancellationToken cancellationToken = default);

    Task<int> FireDueTimersAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<DurableReconciliationResult> ReconcileAsync(
        string tenantId,
        DateTimeOffset heartbeatStaleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<DurableExecutionSnapshot?> GetAsync(
        string tenantId,
        string executionId,
        CancellationToken cancellationToken = default);
}
