using Harness.Persistence.Abstractions.DurableExecution;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDurableExecutionEngine
{
    private static async Task<DurableCommandResult> SignalCoreAsync(
        SqliteConnection connection,
        DurableSignalCommand command,
        CancellationToken cancellationToken)
    {
        var fingerprint = DurableCommandHash.Compute(command);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var inbox = await ReadInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            fingerprint,
            cancellationToken);
        if (inbox is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return inbox;
        }

        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            command.TenantId,
            command.ExecutionId,
            cancellationToken);
        DurableCommandResult result;
        if (snapshot is null)
        {
            result = new DurableCommandResult(DurableCommandStatus.NotFound);
        }
        else if (DurableExecutionStateMachine.IsTerminal(snapshot.State))
        {
            result = new DurableCommandResult(
                DurableCommandStatus.InvalidState,
                snapshot.Version,
                snapshot.State);
        }
        else
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO durable_signals
                        (execution_id, idempotency_key, signal_name, payload_json, received_at)
                    VALUES
                        ($executionId, $idempotencyKey, $signalName, $payloadJson, $receivedAt);
                    """;
                Add(insert, "$executionId", command.ExecutionId);
                Add(insert, "$idempotencyKey", command.IdempotencyKey);
                Add(insert, "$signalName", command.SignalName);
                Add(insert, "$payloadJson", command.PayloadJson);
                Add(insert, "$receivedAt", ToStorage(command.OccurredAt));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            if (snapshot.State == DurableExecutionState.WaitingForSignal)
            {
                var nextVersion = snapshot.Version + 1;
                await UpdateExecutionStateAsync(
                    connection,
                    transaction,
                    command.TenantId,
                    command.ExecutionId,
                    DurableExecutionState.Ready,
                    nextVersion,
                    command.OccurredAt,
                    command.OccurredAt,
                    cancellationToken);
                await AppendTransitionAsync(
                    connection,
                    transaction,
                    command.ExecutionId,
                    snapshot.State,
                    DurableExecutionState.Ready,
                    "signal.received",
                    attemptId: null,
                    command.OccurredAt,
                    cancellationToken);
                result = new DurableCommandResult(
                    DurableCommandStatus.Applied,
                    nextVersion,
                    DurableExecutionState.Ready);
            }
            else
            {
                result = new DurableCommandResult(
                    DurableCommandStatus.Applied,
                    snapshot.Version,
                    snapshot.State);
            }
        }

        await WriteInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            fingerprint,
            result,
            command.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<DurableCommandResult> ScheduleTimerCoreAsync(
        SqliteConnection connection,
        DurableTimerCommand command,
        CancellationToken cancellationToken)
    {
        var fingerprint = DurableCommandHash.Compute(command);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var inbox = await ReadInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            fingerprint,
            cancellationToken);
        if (inbox is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return inbox;
        }

        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            command.TenantId,
            command.ExecutionId,
            cancellationToken);
        DurableCommandResult result;
        if (snapshot is null)
        {
            result = new DurableCommandResult(DurableCommandStatus.NotFound);
        }
        else if (DurableExecutionStateMachine.IsTerminal(snapshot.State))
        {
            result = new DurableCommandResult(
                DurableCommandStatus.InvalidState,
                snapshot.Version,
                snapshot.State);
        }
        else
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO durable_timers
                        (execution_id, timer_id, due_at, payload_json, created_at)
                    VALUES
                        ($executionId, $timerId, $dueAt, $payloadJson, $createdAt);
                    """;
                Add(insert, "$executionId", command.ExecutionId);
                Add(insert, "$timerId", command.TimerId);
                Add(insert, "$dueAt", ToStorage(command.DueAt));
                Add(insert, "$payloadJson", command.PayloadJson);
                Add(insert, "$createdAt", ToStorage(command.OccurredAt));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var state = snapshot.State;
            var version = snapshot.Version;
            if (snapshot.State == DurableExecutionState.Running)
            {
                if (snapshot.ActiveAttemptId is not null)
                {
                    await using var finishAttempt = connection.CreateCommand();
                    finishAttempt.Transaction = transaction;
                    finishAttempt.CommandText =
                        """
                        UPDATE durable_attempts
                        SET state = 'completed', ended_at = $occurredAt, version = version + 1
                        WHERE id = $attemptId AND state = 'running';
                        """;
                    Add(finishAttempt, "$occurredAt", ToStorage(command.OccurredAt));
                    Add(finishAttempt, "$attemptId", snapshot.ActiveAttemptId);
                    await finishAttempt.ExecuteNonQueryAsync(cancellationToken);
                }

                state = DurableExecutionState.WaitingForSignal;
                version++;
                await UpdateExecutionStateAsync(
                    connection,
                    transaction,
                    command.TenantId,
                    command.ExecutionId,
                    state,
                    version,
                    command.DueAt,
                    command.OccurredAt,
                    cancellationToken,
                    invalidateActiveAttempt: true);
                await AppendTransitionAsync(
                    connection,
                    transaction,
                    command.ExecutionId,
                    snapshot.State,
                    state,
                    "timer.scheduled",
                    snapshot.ActiveAttemptId,
                    command.OccurredAt,
                    cancellationToken);
            }

            result = new DurableCommandResult(DurableCommandStatus.Applied, version, state);
        }

        await WriteInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            fingerprint,
            result,
            command.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<int> FireDueTimersCoreAsync(
        SqliteConnection connection,
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var dueTimers = new List<(string ExecutionId, string TimerId, DurableExecutionState State, long Version)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT t.execution_id, t.timer_id, e.state, e.version
                FROM durable_timers t
                JOIN durable_executions e ON e.id = t.execution_id
                WHERE e.tenant_id = $tenantId
                  AND t.fired_at IS NULL
                  AND t.due_at <= $now
                ORDER BY t.due_at, t.execution_id, t.timer_id;
                """;
            Add(select, "$tenantId", tenantId);
            Add(select, "$now", ToStorage(now));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dueTimers.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    DurableExecutionStateCodec.Parse(reader.GetString(2)),
                    reader.GetInt64(3)));
            }
        }

        foreach (var timer in dueTimers)
        {
            await using (var fire = connection.CreateCommand())
            {
                fire.Transaction = transaction;
                fire.CommandText =
                    """
                    UPDATE durable_timers
                    SET fired_at = $now
                    WHERE execution_id = $executionId AND timer_id = $timerId AND fired_at IS NULL;
                    """;
                Add(fire, "$now", ToStorage(now));
                Add(fire, "$executionId", timer.ExecutionId);
                Add(fire, "$timerId", timer.TimerId);
                await fire.ExecuteNonQueryAsync(cancellationToken);
            }

            if (timer.State == DurableExecutionState.WaitingForSignal)
            {
                await UpdateExecutionStateAsync(
                    connection,
                    transaction,
                    tenantId,
                    timer.ExecutionId,
                    DurableExecutionState.Ready,
                    timer.Version + 1,
                    now,
                    now,
                    cancellationToken);
                await AppendTransitionAsync(
                    connection,
                    transaction,
                    timer.ExecutionId,
                    timer.State,
                    DurableExecutionState.Ready,
                    "timer.fired",
                    attemptId: null,
                    now,
                    cancellationToken);
            }
        }

        var dueRetries = new List<(string ExecutionId, long Version)>();
        await using (var retries = connection.CreateCommand())
        {
            retries.Transaction = transaction;
            retries.CommandText =
                """
                SELECT id, version
                FROM durable_executions
                WHERE tenant_id = $tenantId
                  AND state = 'waiting_retry'
                  AND available_at <= $now
                ORDER BY available_at, id;
                """;
            Add(retries, "$tenantId", tenantId);
            Add(retries, "$now", ToStorage(now));
            await using var reader = await retries.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dueRetries.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        foreach (var retry in dueRetries)
        {
            await UpdateExecutionStateAsync(
                connection,
                transaction,
                tenantId,
                retry.ExecutionId,
                DurableExecutionState.Ready,
                retry.Version + 1,
                now,
                now,
                cancellationToken);
            await AppendTransitionAsync(
                connection,
                transaction,
                retry.ExecutionId,
                DurableExecutionState.WaitingForRetry,
                DurableExecutionState.Ready,
                "retry.timerFired",
                attemptId: null,
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return dueTimers.Count + dueRetries.Count;
    }

    private static async Task<DurableReconciliationResult> ReconcileCoreAsync(
        SqliteConnection connection,
        string tenantId,
        DateTimeOffset heartbeatStaleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var stale = new List<StaleAttempt>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT e.id, e.version, e.attempt_count, e.max_attempts,
                       e.retry_initial_ms, e.retry_multiplier, e.retry_maximum_ms, a.id
                FROM durable_executions e
                JOIN durable_attempts a ON a.id = e.active_attempt_id
                WHERE e.tenant_id = $tenantId
                  AND e.state = 'running'
                  AND a.state = 'running'
                  AND (a.lease_expires_at <= $now OR a.last_heartbeat_at < $staleBefore)
                ORDER BY e.id;
                """;
            Add(select, "$tenantId", tenantId);
            Add(select, "$now", ToStorage(now));
            Add(select, "$staleBefore", ToStorage(heartbeatStaleBefore));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stale.Add(new StaleAttempt(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4),
                    decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetInt64(6),
                    reader.GetString(7)));
            }
        }

        var requeued = 0;
        var deadLettered = 0;
        foreach (var item in stale)
        {
            var target = item.AttemptCount >= item.MaxAttempts
                ? DurableExecutionState.DeadLetter
                : DurableExecutionState.WaitingForRetry;
            var availableAt = now;
            if (target == DurableExecutionState.WaitingForRetry)
            {
                var policy = new DurableRetryPolicy(
                    item.MaxAttempts,
                    TimeSpan.FromMilliseconds(item.RetryInitialMilliseconds),
                    item.RetryMultiplier,
                    TimeSpan.FromMilliseconds(item.RetryMaximumMilliseconds));
                availableAt = now.Add(policy.DelayAfterFailure(item.AttemptCount));
                requeued++;
            }
            else
            {
                deadLettered++;
            }

            await using (var abandon = connection.CreateCommand())
            {
                abandon.Transaction = transaction;
                abandon.CommandText =
                    """
                    UPDATE durable_attempts
                    SET state = 'abandoned', ended_at = $now,
                        error_code = 'heartbeat_stale', error_detail = 'Lease or heartbeat expired.',
                        version = version + 1
                    WHERE id = $attemptId AND state = 'running';
                    """;
                Add(abandon, "$now", ToStorage(now));
                Add(abandon, "$attemptId", item.AttemptId);
                await abandon.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpdateExecutionStateAsync(
                connection,
                transaction,
                tenantId,
                item.ExecutionId,
                target,
                item.Version + 1,
                availableAt,
                now,
                cancellationToken,
                invalidateActiveAttempt: true,
                lastError: "Lease or heartbeat expired.");
            if (target == DurableExecutionState.DeadLetter)
            {
                await using var deadLetter = connection.CreateCommand();
                deadLetter.Transaction = transaction;
                deadLetter.CommandText =
                    """
                    INSERT INTO durable_dead_letters
                        (execution_id, attempt_id, error_code, error_detail, dead_lettered_at)
                    VALUES
                        ($executionId, $attemptId, 'heartbeat_stale', 'Lease or heartbeat expired.', $now);
                    """;
                Add(deadLetter, "$executionId", item.ExecutionId);
                Add(deadLetter, "$attemptId", item.AttemptId);
                Add(deadLetter, "$now", ToStorage(now));
                await deadLetter.ExecuteNonQueryAsync(cancellationToken);
            }

            await AppendTransitionAsync(
                connection,
                transaction,
                item.ExecutionId,
                DurableExecutionState.Running,
                target,
                "attempt.reconciled",
                item.AttemptId,
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DurableReconciliationResult(
            requeued,
            deadLettered,
            stale.Select(item => item.ExecutionId).ToArray());
    }

    private static async Task UpdateExecutionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string executionId,
        DurableExecutionState state,
        long version,
        DateTimeOffset availableAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken,
        bool invalidateActiveAttempt = false,
        string? lastError = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE durable_executions
            SET state = $state,
                active_attempt_id = CASE WHEN $invalidate = 1 THEN NULL ELSE active_attempt_id END,
                fencing_token = CASE WHEN $invalidate = 1 THEN fencing_token + 1 ELSE fencing_token END,
                available_at = $availableAt,
                last_error = COALESCE($lastError, last_error),
                version = $version,
                updated_at = $updatedAt
            WHERE id = $executionId AND tenant_id = $tenantId;
            """;
        Add(command, "$state", DurableExecutionStateCodec.ToStorage(state));
        Add(command, "$invalidate", invalidateActiveAttempt ? 1 : 0);
        Add(command, "$availableAt", ToStorage(availableAt));
        Add(command, "$lastError", lastError is null ? DBNull.Value : lastError);
        Add(command, "$version", version);
        Add(command, "$updatedAt", ToStorage(updatedAt));
        Add(command, "$executionId", executionId);
        Add(command, "$tenantId", tenantId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record StaleAttempt(
        string ExecutionId,
        long Version,
        int AttemptCount,
        int MaxAttempts,
        long RetryInitialMilliseconds,
        decimal RetryMultiplier,
        long RetryMaximumMilliseconds,
        string AttemptId);
}
