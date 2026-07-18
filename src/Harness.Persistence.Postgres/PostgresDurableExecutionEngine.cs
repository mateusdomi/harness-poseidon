using Harness.Persistence.Abstractions.DurableExecution;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDurableExecutionEngine(NpgsqlDataSource dataSource) : IDurableExecutionEngine
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<string>> ListMaintenanceTenantsAsync(
        CancellationToken cancellationToken = default)
    {
        var tenants = new List<string>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT tenant_id
            FROM harness.durable_executions
            WHERE state IN ('running','waiting_retry','waiting_signal')
            ORDER BY tenant_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(reader.GetString(0).TrimEnd());
        }

        return tenants;
    }

    public Task<DurableCommandResult> StartAsync(
        DurableExecutionStartRequest request,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(request);
        return StartCoreAsync(request, occurredAt, cancellationToken);
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
        return TryAcquireNextCoreAsync(tenantId, owner, now, leaseDuration, cancellationToken);
    }

    public Task<DurableExecutionSnapshot?> GetAsync(
        string tenantId,
        string executionId,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.ValidateTenantAndExecution(tenantId, executionId);
        return GetCoreAsync(tenantId, executionId, cancellationToken);
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
        return CheckpointCoreAsync(command, cancellationToken);
    }

    public Task<DurableCommandResult> CompleteAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return CompleteCoreAsync(command, cancellationToken);
    }

    public Task<DurableCommandResult> FailAsync(
        DurableFailureCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return FailCoreAsync(command, cancellationToken);
    }

    public Task<DurableCommandResult> SignalAsync(
        DurableSignalCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return SignalCoreAsync(command, cancellationToken);
    }

    public Task<DurableCommandResult> ScheduleTimerAsync(
        DurableTimerCommand command,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.Validate(command);
        return ScheduleTimerCoreAsync(command, cancellationToken);
    }

    public Task<int> FireDueTimersAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.ValidateTenant(tenantId);
        return FireDueTimersCoreAsync(tenantId, now, cancellationToken);
    }

    public Task<DurableReconciliationResult> ReconcileAsync(
        string tenantId,
        DateTimeOffset heartbeatStaleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        DurableExecutionContractValidator.ValidateTenant(tenantId);
        return ReconcileCoreAsync(tenantId, heartbeatStaleBefore, now, cancellationToken);
    }
}
