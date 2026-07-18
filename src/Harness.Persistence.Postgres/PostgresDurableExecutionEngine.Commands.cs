using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Harness.Persistence.Abstractions.DurableExecution;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDurableExecutionEngine
{
    private Task<DurableCommandResult> ChangeLifecycleAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DurableExecutionState target,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        DurableExecutionContractValidator.ValidateTenantAndExecution(tenantId, executionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        DurableExecutionContractValidator.ValidateIdempotencyKey(idempotencyKey, nameof(idempotencyKey));
        return ChangeLifecycleCoreAsync(
            tenantId,
            executionId,
            expectedVersion,
            target,
            occurredAt,
            idempotencyKey,
            cancellationToken);
    }

    private Task<DurableCommandResult> ExecuteLeaseAsync(
        DurableLeaseCommand command,
        TimeSpan? leaseDuration,
        bool renew,
        CancellationToken cancellationToken)
    {
        DurableExecutionContractValidator.Validate(command);
        if (renew)
        {
            DurableExecutionContractValidator.ValidateLeaseDuration(
                leaseDuration ?? throw new ArgumentNullException(nameof(leaseDuration)));
        }

        return ExecuteLeaseCoreAsync(command, leaseDuration, cancellationToken);
    }

    private async Task<DurableCommandResult> ChangeLifecycleCoreAsync(
        string tenantId,
        string executionId,
        long expectedVersion,
        DurableExecutionState target,
        DateTimeOffset occurredAt,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var fingerprint = DurableCommandHash.Compute(new
        {
            operation = "lifecycle",
            tenantId,
            executionId,
            expectedVersion,
            target,
            occurredAt,
        });
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockExecutionAsync(connection, transaction, executionId, cancellationToken);
        var inbox = await ReadInboxAsync(
            connection,
            transaction,
            tenantId,
            idempotencyKey,
            fingerprint,
            cancellationToken);
        if (inbox is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return inbox;
        }

        var snapshot = await ReadSnapshotAsync(connection, transaction, tenantId, executionId, cancellationToken);
        DurableCommandResult result;
        if (snapshot is null)
        {
            result = new DurableCommandResult(DurableCommandStatus.NotFound);
        }
        else if (snapshot.Version != expectedVersion)
        {
            result = new DurableCommandResult(
                DurableCommandStatus.VersionConflict,
                snapshot.Version,
                snapshot.State);
        }
        else if (!DurableExecutionStateMachine.CanTransition(snapshot.State, target))
        {
            result = new DurableCommandResult(
                DurableCommandStatus.InvalidState,
                snapshot.Version,
                snapshot.State);
        }
        else
        {
            if (snapshot.ActiveAttemptId is not null)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE harness.durable_attempts
                    SET state = 'cancelled', ended_at = $1, version = version + 1
                    WHERE id = $2 AND state = 'running';
                    """,
                    cancellationToken,
                    Timestamp(occurredAt),
                    Text(snapshot.ActiveAttemptId));
            }

            var nextVersion = snapshot.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.durable_executions
                SET state = $1,
                    active_attempt_id = NULL,
                    fencing_token = CASE WHEN active_attempt_id IS NULL THEN fencing_token ELSE fencing_token + 1 END,
                    available_at = CASE WHEN $1 = 'ready' THEN $2 ELSE available_at END,
                    version = $3,
                    updated_at = $2
                WHERE id = $4 AND tenant_id = $5 AND version = $6;
                """,
                cancellationToken,
                Text(DurableExecutionStateCodec.ToStorage(target)),
                Timestamp(occurredAt),
                Bigint(nextVersion),
                Text(executionId),
                Text(tenantId),
                Bigint(expectedVersion));
            await AppendTransitionAsync(
                connection,
                transaction,
                executionId,
                snapshot.State,
                target,
                target switch
                {
                    DurableExecutionState.Ready => "execution.resumed",
                    DurableExecutionState.Paused => "execution.paused",
                    DurableExecutionState.Cancelled => "execution.cancelled",
                    _ => "execution.stateChanged",
                },
                snapshot.ActiveAttemptId,
                occurredAt,
                cancellationToken);
            result = new DurableCommandResult(DurableCommandStatus.Applied, nextVersion, target);
        }

        await WriteInboxAsync(
            connection,
            transaction,
            tenantId,
            idempotencyKey,
            fingerprint,
            result,
            occurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<DurableCommandResult> ExecuteLeaseCoreAsync(
        DurableLeaseCommand command,
        TimeSpan? leaseDuration,
        CancellationToken cancellationToken)
    {
        var fingerprint = DurableCommandHash.Compute(new { command, leaseDuration });
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

        var lease = await ReadLeaseAsync(connection, transaction, command, cancellationToken);
        DurableCommandResult result;
        if (!IsLeaseValid(lease, command.Owner, command.FencingToken, command.OccurredAt))
        {
            result = new DurableCommandResult(
                DurableCommandStatus.LeaseRejected,
                lease?.ExecutionVersion,
                lease?.ExecutionState);
        }
        else
        {
            if (leaseDuration is null)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE harness.durable_attempts
                    SET last_heartbeat_at = $1, version = version + 1
                    WHERE id = $2;
                    """,
                    cancellationToken,
                    Timestamp(command.OccurredAt),
                    Text(command.AttemptId));
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE harness.durable_attempts
                    SET last_heartbeat_at = $1, lease_expires_at = $2, version = version + 1
                    WHERE id = $3;
                    """,
                    cancellationToken,
                    Timestamp(command.OccurredAt),
                    Timestamp(command.OccurredAt.Add(leaseDuration.Value)),
                    Text(command.AttemptId));
            }

            var nextVersion = lease!.ExecutionVersion + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.durable_executions
                SET version = $1, updated_at = $2
                WHERE id = $3 AND tenant_id = $4;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.ExecutionId),
                Text(command.TenantId));
            result = new DurableCommandResult(
                DurableCommandStatus.Applied,
                nextVersion,
                DurableExecutionState.Running);
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

    private async Task<DurableCommandResult> CheckpointCoreAsync(
        DurableCheckpointCommand command,
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

        var leaseCommand = new DurableLeaseCommand(
            command.TenantId,
            command.ExecutionId,
            command.AttemptId,
            command.Owner,
            command.FencingToken,
            command.OccurredAt,
            command.IdempotencyKey);
        var lease = await ReadLeaseAsync(connection, transaction, leaseCommand, cancellationToken);
        DurableCommandResult result;
        if (!IsLeaseValid(lease, command.Owner, command.FencingToken, command.OccurredAt))
        {
            result = new DurableCommandResult(
                DurableCommandStatus.LeaseRejected,
                lease?.ExecutionVersion,
                lease?.ExecutionState);
        }
        else
        {
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            string? existingHash;
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText =
                    """
                    SELECT payload_hash FROM harness.durable_checkpoints
                    WHERE execution_id = $1 AND checkpoint_key = $2;
                    """;
                existing.Parameters.Add(Text(command.ExecutionId));
                existing.Parameters.Add(Text(command.CheckpointKey));
                existingHash = (string?)await existing.ExecuteScalarAsync(cancellationToken);
            }

            if (existingHash is not null)
            {
                result = new DurableCommandResult(
                    string.Equals(existingHash, payloadHash, StringComparison.Ordinal)
                        ? DurableCommandStatus.IdempotentReplay
                        : DurableCommandStatus.IdempotencyConflict,
                    lease!.ExecutionVersion,
                    lease.ExecutionState);
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO harness.durable_checkpoints
                        (execution_id, attempt_id, checkpoint_key, payload_json, payload_hash, created_at)
                    VALUES ($1, $2, $3, $4, $5, $6);
                    """,
                    cancellationToken,
                    Text(command.ExecutionId),
                    Text(command.AttemptId),
                    Text(command.CheckpointKey),
                    Json(command.PayloadJson),
                    Text(payloadHash),
                    Timestamp(command.OccurredAt));
                await TouchLeaseAsync(
                    connection,
                    transaction,
                    command.AttemptId,
                    command.ExecutionId,
                    command.OccurredAt,
                    cancellationToken);
                result = new DurableCommandResult(
                    DurableCommandStatus.Applied,
                    lease!.ExecutionVersion + 1,
                    DurableExecutionState.Running);
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

    private Task<DurableCommandResult> CompleteCoreAsync(
        DurableLeaseCommand command,
        CancellationToken cancellationToken) =>
        FinishAttemptAsync(command, failure: null, cancellationToken);

    private Task<DurableCommandResult> FailCoreAsync(
        DurableFailureCommand command,
        CancellationToken cancellationToken)
    {
        var leaseCommand = new DurableLeaseCommand(
            command.TenantId,
            command.ExecutionId,
            command.AttemptId,
            command.Owner,
            command.FencingToken,
            command.OccurredAt,
            command.IdempotencyKey);
        return FinishAttemptAsync(leaseCommand, command, cancellationToken);
    }

    private async Task<DurableCommandResult> FinishAttemptAsync(
        DurableLeaseCommand command,
        DurableFailureCommand? failure,
        CancellationToken cancellationToken)
    {
        var fingerprint = failure is null
            ? DurableCommandHash.Compute(new { operation = "complete", command })
            : DurableCommandHash.Compute(failure);
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

        var lease = await ReadLeaseAsync(connection, transaction, command, cancellationToken);
        DurableCommandResult result;
        if (!IsLeaseValid(lease, command.Owner, command.FencingToken, command.OccurredAt))
        {
            result = new DurableCommandResult(
                DurableCommandStatus.LeaseRejected,
                lease?.ExecutionVersion,
                lease?.ExecutionState);
        }
        else
        {
            var target = DurableExecutionState.Completed;
            var availableAt = command.OccurredAt;
            if (failure is not null)
            {
                target = lease!.AttemptCount >= lease.MaxAttempts
                    ? DurableExecutionState.DeadLetter
                    : DurableExecutionState.WaitingForRetry;
                if (target == DurableExecutionState.WaitingForRetry)
                {
                    var policy = new DurableRetryPolicy(
                        lease.MaxAttempts,
                        TimeSpan.FromMilliseconds(lease.RetryInitialMilliseconds),
                        lease.RetryMultiplier,
                        TimeSpan.FromMilliseconds(lease.RetryMaximumMilliseconds));
                    availableAt = command.OccurredAt.Add(policy.DelayAfterFailure(lease.AttemptCount));
                }
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.durable_attempts
                SET state = $1, ended_at = $2, error_code = $3, error_detail = $4,
                    version = version + 1
                WHERE id = $5;
                """,
                cancellationToken,
                Text(failure is null ? "completed" : "failed"),
                Timestamp(command.OccurredAt),
                NullableText(failure?.ErrorCode),
                NullableText(failure?.ErrorDetail),
                Text(command.AttemptId));
            var nextVersion = lease!.ExecutionVersion + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.durable_executions
                SET state = $1, active_attempt_id = NULL, fencing_token = fencing_token + 1,
                    available_at = $2, last_error = $3, version = $4, updated_at = $5
                WHERE id = $6 AND tenant_id = $7;
                """,
                cancellationToken,
                Text(DurableExecutionStateCodec.ToStorage(target)),
                Timestamp(availableAt),
                NullableText(failure?.ErrorDetail),
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.ExecutionId),
                Text(command.TenantId));
            if (target == DurableExecutionState.DeadLetter)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO harness.durable_dead_letters
                        (execution_id, attempt_id, error_code, error_detail, dead_lettered_at)
                    VALUES ($1, $2, $3, $4, $5);
                    """,
                    cancellationToken,
                    Text(command.ExecutionId),
                    Text(command.AttemptId),
                    Text(failure!.ErrorCode),
                    Text(failure.ErrorDetail),
                    Timestamp(command.OccurredAt));
            }

            await AppendTransitionAsync(
                connection,
                transaction,
                command.ExecutionId,
                DurableExecutionState.Running,
                target,
                failure is null ? "attempt.completed" : "attempt.failed",
                command.AttemptId,
                command.OccurredAt,
                cancellationToken);
            result = new DurableCommandResult(DurableCommandStatus.Applied, nextVersion, target);
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

    private static async Task<LeaseRow?> ReadLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableLeaseCommand command,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT e.state, e.version, e.attempt_count, e.max_attempts,
                   e.retry_initial_ms, e.retry_multiplier, e.retry_maximum_ms,
                   a.state, a.owner, a.fencing_token, a.lease_expires_at
            FROM harness.durable_executions e
            JOIN harness.durable_attempts a ON a.id = e.active_attempt_id
            WHERE e.tenant_id = $1 AND e.id = $2 AND a.id = $3
            FOR UPDATE OF e, a;
            """;
        query.Parameters.Add(Text(command.TenantId));
        query.Parameters.Add(Text(command.ExecutionId));
        query.Parameters.Add(Text(command.AttemptId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LeaseRow(
            DurableExecutionStateCodec.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.GetDecimal(5),
            reader.GetInt64(6),
            DurableAttemptStateCodec.Parse(reader.GetString(7)),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetFieldValue<DateTimeOffset>(10));
    }

    private static bool IsLeaseValid(
        LeaseRow? lease,
        string owner,
        long fencingToken,
        DateTimeOffset occurredAt) =>
        lease is not null && lease.ExecutionState == DurableExecutionState.Running &&
        lease.AttemptState == DurableAttemptState.Running &&
        string.Equals(lease.Owner, owner, StringComparison.Ordinal) &&
        lease.FencingToken == fencingToken && lease.LeaseExpiresAt > occurredAt;

    private static async Task TouchLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string attemptId,
        string executionId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE harness.durable_attempts
            SET last_heartbeat_at = $1, version = version + 1 WHERE id = $2;
            """,
            cancellationToken,
            Timestamp(occurredAt),
            Text(attemptId));
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE harness.durable_executions
            SET version = version + 1, updated_at = $1 WHERE id = $2;
            """,
            cancellationToken,
            Timestamp(occurredAt),
            Text(executionId));
    }

    private static Task LockExecutionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string executionId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text(executionId));

    private sealed record LeaseRow(
        DurableExecutionState ExecutionState,
        long ExecutionVersion,
        int AttemptCount,
        int MaxAttempts,
        long RetryInitialMilliseconds,
        decimal RetryMultiplier,
        long RetryMaximumMilliseconds,
        DurableAttemptState AttemptState,
        string Owner,
        long FencingToken,
        DateTimeOffset LeaseExpiresAt);
}
