using Harness.Persistence.Abstractions.DurableExecution;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDurableExecutionEngine
{
    private async Task<DurableCommandResult> StartCoreAsync(
        DurableExecutionStartRequest request,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"{request.TenantId}:{request.IdempotencyKey}"));
        await LockExecutionAsync(
            connection,
            transaction,
            request.ExecutionId,
            cancellationToken);
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

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.durable_executions
                (id, tenant_id, project_id, state, payload_json, max_attempts,
                 retry_initial_ms, retry_multiplier, retry_maximum_ms, attempt_count,
                 available_at, fencing_token, version, created_at, updated_at)
            VALUES
                ($1, $2, $3, 'ready', $4, $5, $6, $7, $8, 0, $9, 0, 1, $10, $10);
            """,
            cancellationToken,
            Text(request.ExecutionId),
            Text(request.TenantId),
            Text(request.ProjectId),
            Json(request.PayloadJson),
            Integer(request.RetryPolicy.MaxAttempts),
            Bigint(ToMilliseconds(request.RetryPolicy.InitialDelay)),
            Numeric(request.RetryPolicy.Multiplier),
            Bigint(ToMilliseconds(request.RetryPolicy.MaximumDelay)),
            Timestamp(request.AvailableAt),
            Timestamp(occurredAt));
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

    private async Task<DurableExecutionLease?> TryAcquireNextCoreAsync(
        string tenantId,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? executionId = null;
        string? projectId = null;
        string? payloadJson = null;
        var attemptCount = 0;
        long fencingToken = 0;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT id, project_id, payload_json::text, attempt_count, fencing_token
                FROM harness.durable_executions
                WHERE tenant_id = $1 AND state = 'ready' AND available_at <= $2
                ORDER BY available_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1;
                """;
            select.Parameters.Add(Text(tenantId));
            select.Parameters.Add(Timestamp(now));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                executionId = reader.GetString(0).TrimEnd();
                projectId = reader.GetString(1).TrimEnd();
                payloadJson = reader.GetString(2);
                attemptCount = reader.GetInt32(3);
                fencingToken = reader.GetInt64(4);
            }
        }

        if (executionId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var attemptId = UlidValue.New(now).ToString();
        var attemptNumber = attemptCount + 1;
        var nextFencingToken = fencingToken + 1;
        var leaseExpiresAt = now.Add(leaseDuration);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE harness.durable_executions
            SET state = 'running', attempt_count = $1, active_attempt_id = $2,
                fencing_token = $3, version = version + 1, updated_at = $4
            WHERE id = $5 AND tenant_id = $6 AND state = 'ready';
            """,
            cancellationToken,
            Integer(attemptNumber),
            Text(attemptId),
            Bigint(nextFencingToken),
            Timestamp(now),
            Text(executionId),
            Text(tenantId));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.durable_attempts
                (id, tenant_id, execution_id, attempt_number, state, owner, fencing_token,
                 lease_expires_at, last_heartbeat_at, started_at, version)
            VALUES ($1, $2, $3, $4, 'running', $5, $6, $7, $8, $8, 1);
            """,
            cancellationToken,
            Text(attemptId),
            Text(tenantId),
            Text(executionId),
            Integer(attemptNumber),
            Text(owner),
            Bigint(nextFencingToken),
            Timestamp(leaseExpiresAt),
            Timestamp(now));
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
            projectId!,
            executionId,
            attemptId,
            attemptNumber,
            owner,
            nextFencingToken,
            leaseExpiresAt,
            payloadJson!,
            checkpoint.Key,
            checkpoint.Payload);
    }
}
