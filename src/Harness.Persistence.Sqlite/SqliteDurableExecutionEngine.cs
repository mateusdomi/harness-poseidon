using Harness.Persistence.Abstractions.DurableExecution;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDurableExecutionEngine(SqliteWriteDispatcher dispatcher) : IDurableExecutionEngine
{
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<DurableCommandResult> StartAsync(
        DurableExecutionStartRequest request,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(request);
        return _dispatcher.ExecuteAsync(
            (connection, token) => StartCoreAsync(connection, request, occurredAt, token),
            cancellationToken);
    }

    public Task<DurableExecutionLease?> TryAcquireNextAsync(
        string tenantId,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.ValidateTenant(tenantId);
        DurableExecutionContractValidator.ValidateOwner(owner);
        DurableExecutionContractValidator.ValidateLeaseDuration(leaseDuration);
        return _dispatcher.ExecuteAsync(
            (connection, token) => TryAcquireNextCoreAsync(
                connection,
                tenantId,
                owner,
                now,
                leaseDuration,
                token),
            cancellationToken);
    }

    public Task<DurableExecutionSnapshot?> GetAsync(
        string tenantId,
        string executionId,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.ValidateTenantAndExecution(tenantId, executionId);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadSnapshotAsync(connection, null, tenantId, executionId, token),
            cancellationToken);
    }

    public Task<DurableCommandResult> PauseAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync(
            tenantId,
            executionId,
            expectedVersion,
            DurableExecutionState.Paused,
            occurredAt,
            idempotencyKey,
            cancellationToken);

    public Task<DurableCommandResult> ResumeAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync(
            tenantId,
            executionId,
            expectedVersion,
            DurableExecutionState.Ready,
            occurredAt,
            idempotencyKey,
            cancellationToken);

    public Task<DurableCommandResult> CancelAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync(
            tenantId,
            executionId,
            expectedVersion,
            DurableExecutionState.Cancelled,
            occurredAt,
            idempotencyKey,
            cancellationToken);

    public Task<DurableCommandResult> RenewLeaseAsync(
        DurableLeaseCommand command,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        ExecuteLeaseAsync(command, leaseDuration, renew: true, cancellationToken);

    public Task<DurableCommandResult> RecordHeartbeatAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteLeaseAsync(command, leaseDuration: null, renew: false, cancellationToken);

    public Task<DurableCommandResult> CheckpointAsync(
        DurableCheckpointCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CheckpointCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DurableCommandResult> CompleteAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => CompleteCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DurableCommandResult> FailAsync(
        DurableFailureCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => FailCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DurableCommandResult> SignalAsync(
        DurableSignalCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => SignalCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<DurableCommandResult> ScheduleTimerAsync(
        DurableTimerCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ScheduleTimerCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<int> FireDueTimersAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.ValidateTenant(tenantId);
        return _dispatcher.ExecuteAsync(
            (connection, token) => FireDueTimersCoreAsync(connection, tenantId, now, token),
            cancellationToken);
    }

    public Task<DurableReconciliationResult> ReconcileAsync(
        string tenantId,
        DateTimeOffset heartbeatStaleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.ValidateTenant(tenantId);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReconcileCoreAsync(
                connection,
                tenantId,
                heartbeatStaleBefore,
                now,
                token),
            cancellationToken);
    }
}
