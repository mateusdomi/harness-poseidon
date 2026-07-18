using Harness.Persistence.Abstractions.DurableExecution;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDurableExecutionEngine
{
    private async Task<DurableCommandResult> SignalCoreAsync(
        DurableSignalCommand command,
        CancellationToken cancellationToken)
    {
        var fingerprint = DurableCommandHash.Compute(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockExecutionAsync(connection, transaction, command.ExecutionId, cancellationToken);
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
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.durable_signals
                    (execution_id, idempotency_key, signal_name, payload_json, received_at)
                VALUES ($1, $2, $3, $4, $5);
                """,
                cancellationToken,
                Text(command.ExecutionId),
                Text(command.IdempotencyKey),
                Text(command.SignalName),
                Json(command.PayloadJson),
                Timestamp(command.OccurredAt));
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

    private async Task<DurableCommandResult> ScheduleTimerCoreAsync(
        DurableTimerCommand command,
        CancellationToken cancellationToken)
    {
        var fingerprint = DurableCommandHash.Compute(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockExecutionAsync(connection, transaction, command.ExecutionId, cancellationToken);
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
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.durable_timers
                    (execution_id, timer_id, due_at, payload_json, created_at)
                VALUES ($1, $2, $3, $4, $5);
                """,
                cancellationToken,
                Text(command.ExecutionId),
                Text(command.TimerId),
                Timestamp(command.DueAt),
                Json(command.PayloadJson),
                Timestamp(command.OccurredAt));
            var state = snapshot.State;
            var version = snapshot.Version;
            if (snapshot.State == DurableExecutionState.Running)
            {
                if (snapshot.ActiveAttemptId is not null)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE harness.durable_attempts
                        SET state = 'completed', ended_at = $1, version = version + 1
                        WHERE id = $2 AND state = 'running';
                        """,
                        cancellationToken,
                        Timestamp(command.OccurredAt),
                        Text(snapshot.ActiveAttemptId));
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

    private async Task<int> FireDueTimersCoreAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dueTimers = new List<(string ExecutionId, string TimerId, DurableExecutionState State, long Version)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT t.execution_id, t.timer_id, e.state, e.version
                FROM harness.durable_timers t
                JOIN harness.durable_executions e ON e.id = t.execution_id
                WHERE e.tenant_id = $1 AND t.fired_at IS NULL AND t.due_at <= $2
                ORDER BY t.due_at, t.execution_id, t.timer_id
                FOR UPDATE OF t, e;
                """;
            select.Parameters.Add(Text(tenantId));
            select.Parameters.Add(Timestamp(now));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dueTimers.Add((
                    reader.GetString(0).TrimEnd(),
                    reader.GetString(1),
                    DurableExecutionStateCodec.Parse(reader.GetString(2)),
                    reader.GetInt64(3)));
            }
        }

        foreach (var timer in dueTimers)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.durable_timers SET fired_at = $1
                WHERE execution_id = $2 AND timer_id = $3 AND fired_at IS NULL;
                """,
                cancellationToken,
                Timestamp(now),
                Text(timer.ExecutionId),
                Text(timer.TimerId));
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

        var retries = new List<(string ExecutionId, long Version)>();
        await using (var selectRetries = connection.CreateCommand())
        {
            selectRetries.Transaction = transaction;
            selectRetries.CommandText =
                """
                SELECT id, version FROM harness.durable_executions
                WHERE tenant_id = $1 AND state = 'waiting_retry' AND available_at <= $2
                ORDER BY available_at, id
                FOR UPDATE;
                """;
            selectRetries.Parameters.Add(Text(tenantId));
            selectRetries.Parameters.Add(Timestamp(now));
            await using var reader = await selectRetries.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                retries.Add((reader.GetString(0).TrimEnd(), reader.GetInt64(1)));
            }
        }

        foreach (var retry in retries)
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
        return dueTimers.Count + retries.Count;
    }

    private async Task<DurableReconciliationResult> ReconcileCoreAsync(
        string tenantId,
        DateTimeOffset heartbeatStaleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stale = new List<StaleAttempt>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT e.id, e.version, e.attempt_count, e.max_attempts,
                       e.retry_initial_ms, e.retry_multiplier, e.retry_maximum_ms, a.id
                FROM harness.durable_executions e
                JOIN harness.durable_attempts a ON a.id = e.active_attempt_id
                WHERE e.tenant_id = $1 AND e.state = 'running' AND a.state = 'running'
                  AND (a.lease_expires_at <= $2 OR a.last_heartbeat_at < $3)
                ORDER BY e.id
                FOR UPDATE OF e, a;
                """;
            select.Parameters.Add(Text(tenantId));
            select.Parameters.Add(Timestamp(now));
            select.Parameters.Add(Timestamp(heartbeatStaleBefore));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stale.Add(new StaleAttempt(
                    reader.GetString(0).TrimEnd(),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4),
                    reader.GetDecimal(5),
                    reader.GetInt64(6),
                    reader.GetString(7).TrimEnd()));
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

            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.durable_attempts
                SET state = 'abandoned', ended_at = $1, error_code = 'heartbeat_stale',
                    error_detail = 'Lease or heartbeat expired.', version = version + 1
                WHERE id = $2 AND state = 'running';
                """,
                cancellationToken,
                Timestamp(now),
                Text(item.AttemptId));
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
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO harness.durable_dead_letters
                        (execution_id, attempt_id, error_code, error_detail, dead_lettered_at)
                    VALUES ($1, $2, 'heartbeat_stale', 'Lease or heartbeat expired.', $3);
                    """,
                    cancellationToken,
                    Text(item.ExecutionId),
                    Text(item.AttemptId),
                    Timestamp(now));
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

    private static Task UpdateExecutionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string executionId,
        DurableExecutionState state,
        long version,
        DateTimeOffset availableAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken,
        bool invalidateActiveAttempt = false,
        string? lastError = null) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE harness.durable_executions
            SET state = $1,
                active_attempt_id = CASE WHEN $2 THEN NULL ELSE active_attempt_id END,
                fencing_token = CASE WHEN $2 THEN fencing_token + 1 ELSE fencing_token END,
                available_at = $3,
                last_error = COALESCE($4, last_error),
                version = $5,
                updated_at = $6
            WHERE id = $7 AND tenant_id = $8;
            """,
            cancellationToken,
            Text(DurableExecutionStateCodec.ToStorage(state)),
            Boolean(invalidateActiveAttempt),
            Timestamp(availableAt),
            NullableText(lastError),
            Bigint(version),
            Timestamp(updatedAt),
            Text(executionId),
            Text(tenantId));

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
