using System.Security.Cryptography;
using System.Text;
using Harness.Persistence.Abstractions.DurableExecution;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDurableExecutionEngine
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
        return _dispatcher.ExecuteAsync(
            (connection, token) => ChangeLifecycleCoreAsync(
                connection,
                tenantId,
                executionId,
                expectedVersion,
                target,
                occurredAt,
                idempotencyKey,
                token),
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

        return _dispatcher.ExecuteAsync(
            (connection, token) => ExecuteLeaseCoreAsync(
                connection,
                command,
                leaseDuration,
                token),
            cancellationToken);
    }

    private static async Task<DurableCommandResult> ChangeLifecycleCoreAsync(
        SqliteConnection connection,
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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

        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            tenantId,
            executionId,
            cancellationToken);
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
                await using var cancelAttempt = connection.CreateCommand();
                cancelAttempt.Transaction = transaction;
                cancelAttempt.CommandText =
                    """
                    UPDATE durable_attempts
                    SET state = 'cancelled', ended_at = $occurredAt, version = version + 1
                    WHERE id = $attemptId AND state = 'running';
                    """;
                Add(cancelAttempt, "$occurredAt", ToStorage(occurredAt));
                Add(cancelAttempt, "$attemptId", snapshot.ActiveAttemptId);
                await cancelAttempt.ExecuteNonQueryAsync(cancellationToken);
            }

            var nextVersion = snapshot.Version + 1;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE durable_executions
                    SET state = $state,
                        active_attempt_id = NULL,
                        fencing_token = CASE WHEN active_attempt_id IS NULL THEN fencing_token ELSE fencing_token + 1 END,
                        available_at = CASE WHEN $state = 'ready' THEN $occurredAt ELSE available_at END,
                        version = $nextVersion,
                        updated_at = $occurredAt
                    WHERE id = $executionId AND tenant_id = $tenantId AND version = $expectedVersion;
                    """;
                Add(update, "$state", DurableExecutionStateCodec.ToStorage(target));
                Add(update, "$occurredAt", ToStorage(occurredAt));
                Add(update, "$nextVersion", nextVersion);
                Add(update, "$executionId", executionId);
                Add(update, "$tenantId", tenantId);
                Add(update, "$expectedVersion", expectedVersion);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("Durable lifecycle version changed inside SQLite dispatcher.");
                }
            }

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

    private static async Task<DurableCommandResult> ExecuteLeaseCoreAsync(
        SqliteConnection connection,
        DurableLeaseCommand command,
        TimeSpan? leaseDuration,
        CancellationToken cancellationToken)
    {
        var fingerprint = DurableCommandHash.Compute(new { command, leaseDuration });
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
            await using (var updateAttempt = connection.CreateCommand())
            {
                updateAttempt.Transaction = transaction;
                updateAttempt.CommandText = leaseDuration is null
                    ? """
                      UPDATE durable_attempts
                      SET last_heartbeat_at = $occurredAt, version = version + 1
                      WHERE id = $attemptId;
                      """
                    : """
                      UPDATE durable_attempts
                      SET last_heartbeat_at = $occurredAt,
                          lease_expires_at = $leaseExpiresAt,
                          version = version + 1
                      WHERE id = $attemptId;
                      """;
                Add(updateAttempt, "$occurredAt", ToStorage(command.OccurredAt));
                if (leaseDuration is not null)
                {
                    Add(updateAttempt, "$leaseExpiresAt", ToStorage(command.OccurredAt.Add(leaseDuration.Value)));
                }

                Add(updateAttempt, "$attemptId", command.AttemptId);
                await updateAttempt.ExecuteNonQueryAsync(cancellationToken);
            }

            var nextVersion = lease!.ExecutionVersion + 1;
            await using (var updateExecution = connection.CreateCommand())
            {
                updateExecution.Transaction = transaction;
                updateExecution.CommandText =
                    """
                    UPDATE durable_executions
                    SET version = $version, updated_at = $occurredAt
                    WHERE id = $executionId AND tenant_id = $tenantId;
                    """;
                Add(updateExecution, "$version", nextVersion);
                Add(updateExecution, "$occurredAt", ToStorage(command.OccurredAt));
                Add(updateExecution, "$executionId", command.ExecutionId);
                Add(updateExecution, "$tenantId", command.TenantId);
                await updateExecution.ExecuteNonQueryAsync(cancellationToken);
            }

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

    private static async Task<DurableCommandResult> CheckpointCoreAsync(
        SqliteConnection connection,
        DurableCheckpointCommand command,
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
                    SELECT payload_hash
                    FROM durable_checkpoints
                    WHERE execution_id = $executionId AND checkpoint_key = $checkpointKey;
                    """;
                Add(existing, "$executionId", command.ExecutionId);
                Add(existing, "$checkpointKey", command.CheckpointKey);
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
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO durable_checkpoints
                            (execution_id, attempt_id, checkpoint_key, payload_json, payload_hash, created_at)
                        VALUES
                            ($executionId, $attemptId, $checkpointKey, $payloadJson, $payloadHash, $createdAt);
                        """;
                    Add(insert, "$executionId", command.ExecutionId);
                    Add(insert, "$attemptId", command.AttemptId);
                    Add(insert, "$checkpointKey", command.CheckpointKey);
                    Add(insert, "$payloadJson", command.PayloadJson);
                    Add(insert, "$payloadHash", payloadHash);
                    Add(insert, "$createdAt", ToStorage(command.OccurredAt));
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

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

    private static async Task<DurableCommandResult> CompleteCoreAsync(
        SqliteConnection connection,
        DurableLeaseCommand command,
        CancellationToken cancellationToken) =>
        await FinishAttemptAsync(connection, command, failure: null, cancellationToken);

    private static async Task<DurableCommandResult> FailCoreAsync(
        SqliteConnection connection,
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
        return await FinishAttemptAsync(connection, leaseCommand, command, cancellationToken);
    }

    private static async Task<DurableCommandResult> FinishAttemptAsync(
        SqliteConnection connection,
        DurableLeaseCommand command,
        DurableFailureCommand? failure,
        CancellationToken cancellationToken)
    {
        var fingerprint = failure is null
            ? DurableCommandHash.Compute(new { operation = "complete", command })
            : DurableCommandHash.Compute(failure);
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

            await using (var updateAttempt = connection.CreateCommand())
            {
                updateAttempt.Transaction = transaction;
                updateAttempt.CommandText =
                    """
                    UPDATE durable_attempts
                    SET state = $state,
                        ended_at = $occurredAt,
                        error_code = $errorCode,
                        error_detail = $errorDetail,
                        version = version + 1
                    WHERE id = $attemptId;
                    """;
                Add(updateAttempt, "$state", failure is null ? "completed" : "failed");
                Add(updateAttempt, "$occurredAt", ToStorage(command.OccurredAt));
                Add(updateAttempt, "$errorCode", failure is null ? DBNull.Value : failure.ErrorCode);
                Add(updateAttempt, "$errorDetail", failure is null ? DBNull.Value : failure.ErrorDetail);
                Add(updateAttempt, "$attemptId", command.AttemptId);
                await updateAttempt.ExecuteNonQueryAsync(cancellationToken);
            }

            var nextVersion = lease!.ExecutionVersion + 1;
            await using (var updateExecution = connection.CreateCommand())
            {
                updateExecution.Transaction = transaction;
                updateExecution.CommandText =
                    """
                    UPDATE durable_executions
                    SET state = $state,
                        active_attempt_id = NULL,
                        fencing_token = fencing_token + 1,
                        available_at = $availableAt,
                        last_error = $lastError,
                        version = $version,
                        updated_at = $occurredAt
                    WHERE id = $executionId AND tenant_id = $tenantId;
                    """;
                Add(updateExecution, "$state", DurableExecutionStateCodec.ToStorage(target));
                Add(updateExecution, "$availableAt", ToStorage(availableAt));
                Add(updateExecution, "$lastError", failure is null ? DBNull.Value : failure.ErrorDetail);
                Add(updateExecution, "$version", nextVersion);
                Add(updateExecution, "$occurredAt", ToStorage(command.OccurredAt));
                Add(updateExecution, "$executionId", command.ExecutionId);
                Add(updateExecution, "$tenantId", command.TenantId);
                await updateExecution.ExecuteNonQueryAsync(cancellationToken);
            }

            if (target == DurableExecutionState.DeadLetter)
            {
                await using var deadLetter = connection.CreateCommand();
                deadLetter.Transaction = transaction;
                deadLetter.CommandText =
                    """
                    INSERT INTO durable_dead_letters
                        (execution_id, attempt_id, error_code, error_detail, dead_lettered_at)
                    VALUES
                        ($executionId, $attemptId, $errorCode, $errorDetail, $occurredAt);
                    """;
                Add(deadLetter, "$executionId", command.ExecutionId);
                Add(deadLetter, "$attemptId", command.AttemptId);
                Add(deadLetter, "$errorCode", failure!.ErrorCode);
                Add(deadLetter, "$errorDetail", failure.ErrorDetail);
                Add(deadLetter, "$occurredAt", ToStorage(command.OccurredAt));
                await deadLetter.ExecuteNonQueryAsync(cancellationToken);
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
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableLeaseCommand command,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT e.state,
                   e.version,
                   e.attempt_count,
                   e.max_attempts,
                   e.retry_initial_ms,
                   e.retry_multiplier,
                   e.retry_maximum_ms,
                   a.state,
                   a.owner,
                   a.fencing_token,
                   a.lease_expires_at
            FROM durable_executions e
            JOIN durable_attempts a ON a.id = e.active_attempt_id
            WHERE e.tenant_id = $tenantId
              AND e.id = $executionId
              AND a.id = $attemptId;
            """;
        Add(query, "$tenantId", command.TenantId);
        Add(query, "$executionId", command.ExecutionId);
        Add(query, "$attemptId", command.AttemptId);
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
            decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt64(6),
            DurableAttemptStateCodec.Parse(reader.GetString(7)),
            reader.GetString(8),
            reader.GetInt64(9),
            ParseStorage(reader.GetString(10)));
    }

    private static bool IsLeaseValid(
        LeaseRow? lease,
        string owner,
        long fencingToken,
        DateTimeOffset occurredAt) =>
        lease is not null &&
        lease.ExecutionState == DurableExecutionState.Running &&
        lease.AttemptState == DurableAttemptState.Running &&
        string.Equals(lease.Owner, owner, StringComparison.Ordinal) &&
        lease.FencingToken == fencingToken &&
        lease.LeaseExpiresAt > occurredAt;

    private static async Task TouchLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId,
        string executionId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE durable_attempts
            SET last_heartbeat_at = $occurredAt, version = version + 1
            WHERE id = $attemptId;
            UPDATE durable_executions
            SET version = version + 1, updated_at = $occurredAt
            WHERE id = $executionId;
            """;
        Add(command, "$occurredAt", ToStorage(occurredAt));
        Add(command, "$attemptId", attemptId);
        Add(command, "$executionId", executionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
