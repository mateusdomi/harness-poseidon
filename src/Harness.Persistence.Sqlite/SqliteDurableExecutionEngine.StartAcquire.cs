using System.Globalization;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDurableExecutionEngine
{
    private static async Task<DurableCommandResult> StartCoreAsync(
        SqliteConnection connection,
        DurableExecutionStartRequest request,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var commandHash = DurableCommandHash.Compute(request);
        var inbox = await ReadInboxAsync(
            connection,
            transaction,
            request.TenantId,
            request.IdempotencyKey,
            commandHash,
            cancellationToken);
        if (inbox is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return inbox;
        }

        var existing = await ReadSnapshotAsync(
            connection,
            transaction,
            request.TenantId,
            request.ExecutionId,
            cancellationToken);
        if (existing is not null)
        {
            var rejected = new DurableCommandResult(
                DurableCommandStatus.InvalidState,
                existing.Version,
                existing.State);
            await WriteInboxAsync(
                connection,
                transaction,
                request.TenantId,
                request.IdempotencyKey,
                commandHash,
                rejected,
                occurredAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rejected;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO durable_executions
                    (id, tenant_id, project_id, state, payload_json, max_attempts,
                     retry_initial_ms, retry_multiplier, retry_maximum_ms, attempt_count,
                     available_at, fencing_token, version, created_at, updated_at)
                VALUES
                    ($id, $tenantId, $projectId, 'ready', $payloadJson, $maxAttempts,
                     $retryInitialMs, $retryMultiplier, $retryMaximumMs, 0,
                     $availableAt, 0, 1, $createdAt, $updatedAt);
                """;
            Add(command, "$id", request.ExecutionId);
            Add(command, "$tenantId", request.TenantId);
            Add(command, "$projectId", request.ProjectId);
            Add(command, "$payloadJson", request.PayloadJson);
            Add(command, "$maxAttempts", request.RetryPolicy.MaxAttempts);
            Add(command, "$retryInitialMs", ToMilliseconds(request.RetryPolicy.InitialDelay));
            Add(command, "$retryMultiplier", request.RetryPolicy.Multiplier.ToString(CultureInfo.InvariantCulture));
            Add(command, "$retryMaximumMs", ToMilliseconds(request.RetryPolicy.MaximumDelay));
            Add(command, "$availableAt", ToStorage(request.AvailableAt));
            Add(command, "$createdAt", ToStorage(occurredAt));
            Add(command, "$updatedAt", ToStorage(occurredAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendTransitionAsync(
            connection,
            transaction,
            request.ExecutionId,
            from: null,
            DurableExecutionState.Ready,
            "execution.started",
            attemptId: null,
            occurredAt,
            cancellationToken);
        var result = new DurableCommandResult(
            DurableCommandStatus.Applied,
            Version: 1,
            DurableExecutionState.Ready);
        await WriteInboxAsync(
            connection,
            transaction,
            request.TenantId,
            request.IdempotencyKey,
            commandHash,
            result,
            occurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<DurableExecutionLease?> TryAcquireNextCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string executionId;
        string projectId;
        string payloadJson;
        int attemptCount;
        long fencingToken;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT id, project_id, payload_json, attempt_count, fencing_token
                FROM durable_executions
                WHERE tenant_id = $tenantId
                  AND state = 'ready'
                  AND available_at <= $now
                ORDER BY available_at, id
                LIMIT 1;
                """;
            Add(select, "$tenantId", tenantId);
            Add(select, "$now", ToStorage(now));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            executionId = reader.GetString(0);
            projectId = reader.GetString(1);
            payloadJson = reader.GetString(2);
            attemptCount = reader.GetInt32(3);
            fencingToken = reader.GetInt64(4);
        }

        var attemptId = UlidValue.New(now).ToString();
        var attemptNumber = attemptCount + 1;
        var nextFencingToken = fencingToken + 1;
        var leaseExpiresAt = now.Add(leaseDuration);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE durable_executions
                SET state = 'running',
                    attempt_count = $attemptNumber,
                    active_attempt_id = $attemptId,
                    fencing_token = $fencingToken,
                    version = version + 1,
                    updated_at = $now
                WHERE id = $executionId AND tenant_id = $tenantId AND state = 'ready';
                """;
            Add(update, "$attemptNumber", attemptNumber);
            Add(update, "$attemptId", attemptId);
            Add(update, "$fencingToken", nextFencingToken);
            Add(update, "$now", ToStorage(now));
            Add(update, "$executionId", executionId);
            Add(update, "$tenantId", tenantId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("SQLite dispatcher lost the selected durable execution.");
            }
        }

        await using (var insertAttempt = connection.CreateCommand())
        {
            insertAttempt.Transaction = transaction;
            insertAttempt.CommandText =
                """
                INSERT INTO durable_attempts
                    (id, tenant_id, execution_id, attempt_number, state, owner, fencing_token,
                     lease_expires_at, last_heartbeat_at, started_at, version)
                VALUES
                    ($id, $tenantId, $executionId, $attemptNumber, 'running', $owner, $fencingToken,
                     $leaseExpiresAt, $now, $now, 1);
                """;
            Add(insertAttempt, "$id", attemptId);
            Add(insertAttempt, "$tenantId", tenantId);
            Add(insertAttempt, "$executionId", executionId);
            Add(insertAttempt, "$attemptNumber", attemptNumber);
            Add(insertAttempt, "$owner", owner);
            Add(insertAttempt, "$fencingToken", nextFencingToken);
            Add(insertAttempt, "$leaseExpiresAt", ToStorage(leaseExpiresAt));
            Add(insertAttempt, "$now", ToStorage(now));
            await insertAttempt.ExecuteNonQueryAsync(cancellationToken);
        }

        var checkpoint = await ReadLatestCheckpointAsync(
            connection,
            transaction,
            executionId,
            cancellationToken);
        await AppendTransitionAsync(
            connection,
            transaction,
            executionId,
            DurableExecutionState.Ready,
            DurableExecutionState.Running,
            "attempt.acquired",
            attemptId,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DurableExecutionLease(
            tenantId,
            projectId,
            executionId,
            attemptId,
            attemptNumber,
            owner,
            nextFencingToken,
            leaseExpiresAt,
            payloadJson,
            checkpoint.Key,
            checkpoint.Payload);
    }
}
