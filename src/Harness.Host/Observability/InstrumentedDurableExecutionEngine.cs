using System.Diagnostics;
using Harness.Persistence.Abstractions.DurableExecution;

namespace Harness.Host.Observability;

internal sealed class InstrumentedDurableExecutionEngine(IDurableExecutionEngine inner)
    : IDurableExecutionEngine
{
    private readonly IDurableExecutionEngine _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<IReadOnlyList<string>> ListMaintenanceTenantsAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "list_maintenance_tenants",
            null,
            null,
            null,
            null,
            () => _inner.ListMaintenanceTenantsAsync(cancellationToken),
            tenants => tenants.Count == 0 ? "empty" : "found",
            cancellationToken);

    public Task<DurableCommandResult> StartAsync(
        DurableExecutionStartRequest request,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "start",
            request.TenantId,
            request.ProjectId,
            request.ExecutionId,
            null,
            () => _inner.StartAsync(request, occurredAt, cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> PauseAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "pause",
            tenantId,
            null,
            executionId,
            null,
            () => _inner.PauseAsync(
                tenantId,
                executionId,
                expectedVersion,
                occurredAt,
                idempotencyKey,
                cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> ResumeAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "resume",
            tenantId,
            null,
            executionId,
            null,
            () => _inner.ResumeAsync(
                tenantId,
                executionId,
                expectedVersion,
                occurredAt,
                idempotencyKey,
                cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> CancelAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "cancel",
            tenantId,
            null,
            executionId,
            null,
            () => _inner.CancelAsync(
                tenantId,
                executionId,
                expectedVersion,
                occurredAt,
                idempotencyKey,
                cancellationToken),
            cancellationToken);

    public Task<DurableExecutionLease?> TryAcquireNextAsync(
        string tenantId,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "acquire",
            tenantId,
            null,
            null,
            null,
            () => _inner.TryAcquireNextAsync(
                tenantId,
                owner,
                now,
                leaseDuration,
                cancellationToken),
            lease => lease is null ? "empty" : "acquired",
            cancellationToken,
            (activity, lease) =>
            {
                if (lease is not null)
                {
                    SetCorrelation(
                        activity,
                        lease.TenantId,
                        lease.ProjectId,
                        lease.ExecutionId,
                        lease.AttemptId);
                    activity?.SetTag("durable.attempt_number", lease.AttemptNumber);
                }
            },
            lease => lease is null ? null : "running");

    public Task<DurableCommandResult> RenewLeaseAsync(
        DurableLeaseCommand command,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "renew_lease",
            command.TenantId,
            null,
            command.ExecutionId,
            command.AttemptId,
            () => _inner.RenewLeaseAsync(command, leaseDuration, cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> RecordHeartbeatAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "heartbeat",
            command.TenantId,
            null,
            command.ExecutionId,
            command.AttemptId,
            () => _inner.RecordHeartbeatAsync(command, cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> CheckpointAsync(
        DurableCheckpointCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "checkpoint",
            command.TenantId,
            null,
            command.ExecutionId,
            command.AttemptId,
            () => _inner.CheckpointAsync(command, cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> CompleteAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "complete",
            command.TenantId,
            null,
            command.ExecutionId,
            command.AttemptId,
            () => _inner.CompleteAsync(command, cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> FailAsync(
        DurableFailureCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "fail",
            command.TenantId,
            null,
            command.ExecutionId,
            command.AttemptId,
            () => _inner.FailAsync(command, cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> SignalAsync(
        DurableSignalCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "signal",
            command.TenantId,
            null,
            command.ExecutionId,
            null,
            () => _inner.SignalAsync(command, cancellationToken),
            cancellationToken);

    public Task<DurableCommandResult> ScheduleTimerAsync(
        DurableTimerCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(
            "schedule_timer",
            command.TenantId,
            null,
            command.ExecutionId,
            null,
            () => _inner.ScheduleTimerAsync(command, cancellationToken),
            cancellationToken);

    public Task<int> FireDueTimersAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "fire_due_timers",
            tenantId,
            null,
            null,
            null,
            () => _inner.FireDueTimersAsync(tenantId, now, cancellationToken),
            count => count == 0 ? "empty" : "fired",
            cancellationToken,
            (activity, count) => activity?.SetTag("durable.timers_fired", count));

    public Task<DurableReconciliationResult> ReconcileAsync(
        string tenantId,
        DateTimeOffset heartbeatStaleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "reconcile",
            tenantId,
            null,
            null,
            null,
            () => _inner.ReconcileAsync(
                tenantId,
                heartbeatStaleBefore,
                now,
                cancellationToken),
            result => result.Requeued == 0 && result.DeadLettered == 0
                ? "unchanged"
                : "recovered",
            cancellationToken,
            (activity, result) =>
            {
                activity?.SetTag("durable.requeued", result.Requeued);
                activity?.SetTag("durable.dead_lettered", result.DeadLettered);
            });

    public Task<DurableExecutionSnapshot?> GetAsync(
        string tenantId,
        string executionId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "get",
            tenantId,
            null,
            executionId,
            null,
            () => _inner.GetAsync(tenantId, executionId, cancellationToken),
            snapshot => snapshot is null ? "not_found" : "found",
            cancellationToken,
            (activity, snapshot) =>
            {
                if (snapshot is not null)
                {
                    SetCorrelation(
                        activity,
                        snapshot.TenantId,
                        snapshot.ProjectId,
                        snapshot.ExecutionId,
                        snapshot.ActiveAttemptId);
                }
            },
            snapshot => snapshot is null
                ? null
                : DurableExecutionStateCodec.ToStorage(snapshot.State));

    private static Task<DurableCommandResult> ExecuteCommandAsync(
        string operation,
        string tenantId,
        string? projectId,
        string executionId,
        string? attemptId,
        Func<Task<DurableCommandResult>> action,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            operation,
            tenantId,
            projectId,
            executionId,
            attemptId,
            action,
            result => result.Status.ToString().ToLowerInvariant(),
            cancellationToken,
            null,
            result => result.State is null
                ? null
                : DurableExecutionStateCodec.ToStorage(result.State.Value));

    private static async Task<T> ExecuteAsync<T>(
        string operation,
        string? tenantId,
        string? projectId,
        string? executionId,
        string? attemptId,
        Func<Task<T>> action,
        Func<T, string> resultSelector,
        CancellationToken cancellationToken,
        Action<Activity?, T>? enrich = null,
        Func<T, string?>? stateSelector = null)
    {
        using var activity = PoseidonTelemetry.ActivitySource.StartActivity(
            $"poseidon.durable.{operation}",
            ActivityKind.Internal);
        SetCorrelation(activity, tenantId, projectId, executionId, attemptId);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var value = await action();
            var result = resultSelector(value);
            var state = stateSelector?.Invoke(value);
            activity?.SetTag("durable.result", result);
            if (state is not null)
            {
                activity?.SetTag("durable.state", state);
            }

            enrich?.Invoke(activity, value);
            PoseidonTelemetry.RecordDurableOperation(
                operation,
                result,
                state,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("durable.result", "cancelled");
            PoseidonTelemetry.RecordDurableOperation(
                operation,
                "cancelled",
                null,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exception.GetType().FullName);
            PoseidonTelemetry.RecordDurableOperation(
                operation,
                "error",
                null,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
    }

    private static void SetCorrelation(
        Activity? activity,
        string? tenantId,
        string? projectId,
        string? executionId,
        string? attemptId)
    {
        activity?.SetTag("tenant_id", tenantId);
        activity?.SetTag("project_id", projectId);
        activity?.SetTag("execution_id", executionId);
        activity?.SetTag("attempt_id", attemptId);
    }
}
