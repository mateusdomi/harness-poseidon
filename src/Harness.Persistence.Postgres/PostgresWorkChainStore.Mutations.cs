using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkChainStore
{
    public Task<WorkChainMutationReceipt> AddInstructionVersionAsync(
        WorkInstructionVersionCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return AddInstructionVersionCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> StartAttemptAsync(
        WorkAttemptStartCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return StartAttemptCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> CompleteAttemptAsync(
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return CompleteAttemptCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ExpireAttemptLeaseAsync(
        WorkAttemptLeaseExpiredCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return ExpireAttemptLeaseCoreAsync(command, cancellationToken);
    }

    public Task<WorkChainMutationReceipt> ReviewAttemptAsync(
        WorkAttemptReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkChainMutationValidator.Validate(command);
        return ReviewAttemptCoreAsync(command, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> AddInstructionVersionCoreAsync(
        WorkInstructionVersionCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, attemptId: null, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, attemptId: null);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, attemptId: null);
        }
        else if (row.TaskState != "ready" || row.LatestAttemptState != "rejected")
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, attemptId: null);
        }
        else
        {
            var instructionVersion = row.LatestInstructionVersion + 1;
            var nextTaskVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.instruction_versions
                    (id, tenant_id, project_id, task_id, version, content, content_hash,
                     supersedes_id, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9);
                """,
                cancellationToken,
                Text(command.InstructionVersionId), Text(command.TenantId), Text(row.ProjectId),
                Text(command.TaskId), Integer(instructionVersion), Text(command.Content),
                Text(command.ContentHash), Text(row.LatestInstructionId), Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET version = $1, updated_at = $2,
                    board_state = 'ready', blocked_reason = NULL
                WHERE id = $3 AND tenant_id = $4 AND version = $5;
                """,
                cancellationToken,
                Bigint(nextTaskVersion), Timestamp(command.OccurredAt), Text(command.TaskId),
                Text(command.TenantId), Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, null,
                nextTaskVersion, "ready", row.LatestAttemptState,
                InstructionVersionId: command.InstructionVersionId,
                InstructionVersion: instructionVersion);
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "task.stateChanged", command.OccurredAt, receipt, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> StartAttemptCoreAsync(
        WorkAttemptStartCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, attemptId: null, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, command.AttemptId);
        }
        else if (row.TaskState != "ready" || row.LatestInstructionId != command.InstructionVersionId ||
            (row.LatestAttemptState == "rejected" && row.LatestAttemptInstructionId == command.InstructionVersionId))
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.work_attempts
                    (id, tenant_id, project_id, task_id, instruction_version_id, attempt_number,
                     producer_agent_id, state, started_at, operational_state)
                VALUES ($1, $2, $3, $4, $5, $6, $7, 'running', $8, 'running');
                """,
                cancellationToken,
                Text(command.AttemptId), Text(command.TenantId), Text(row.ProjectId), Text(command.TaskId),
                Text(command.InstructionVersionId), Integer(row.AttemptCount + 1),
                Text(command.ProducerAgentId), Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.attempt_events (id, tenant_id, project_id, attempt_id, kind, content, occurred_at)
                VALUES ($1, $2, $3, $4, 'log', 'Attempt started.', $5);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId),
                Text(row.ProjectId), Text(command.AttemptId), Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET state = 'running', version = $1, updated_at = $2,
                    board_state = 'development', blocked_reason = NULL
                WHERE id = $3 AND tenant_id = $4 AND version = $5;
                """,
                cancellationToken,
                Bigint(nextVersion), Timestamp(command.OccurredAt), Text(command.TaskId),
                Text(command.TenantId), Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, "running", "running");
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "attempt.started", command.OccurredAt, receipt, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> CompleteAttemptCoreAsync(
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, command.AttemptId, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, command.AttemptId);
        }
        else if (row.TaskState != "running" || row.AttemptState != "running")
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, command.AttemptId);
        }
        else
        {
            for (var index = 0; index < command.Evidence.Count; index++)
            {
                await ExecuteAsync(
                    connection, transaction,
                    """
                    INSERT INTO harness.work_evidence
                        (id, tenant_id, project_id, attempt_id, ordinal, reference, created_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7);
                    """,
                    cancellationToken,
                    Text(command.Evidence[index].EvidenceId), Text(command.TenantId), Text(row.ProjectId),
                    Text(command.AttemptId), Integer(index + 1), Text(command.Evidence[index].Reference),
                    Timestamp(command.OccurredAt));
            }

            await ExecuteAsync(
                connection, transaction,
                "UPDATE harness.work_attempts SET state = 'awaiting_review', completed_at = $1, operational_state = 'completed' WHERE id = $2 AND state = 'running';",
                cancellationToken,
                Timestamp(command.OccurredAt), Text(command.AttemptId));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.attempt_events (id, tenant_id, project_id, attempt_id, kind, content, occurred_at)
                VALUES ($1, $2, $3, $4, 'log', 'Attempt submitted for review.', $5);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId),
                Text(row.ProjectId), Text(command.AttemptId), Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET state = 'awaiting_review', version = $1, updated_at = $2,
                    board_state = 'review', blocked_reason = NULL
                WHERE id = $3 AND tenant_id = $4 AND version = $5;
                """,
                cancellationToken,
                Bigint(nextVersion), Timestamp(command.OccurredAt), Text(command.TaskId),
                Text(command.TenantId), Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, "awaiting_review", "awaiting_review");
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "attempt.completed", command.OccurredAt, receipt, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> ReviewAttemptCoreAsync(
        WorkAttemptReviewCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection, transaction, command.TenantId, command.SolicitationId,
            command.TaskId, command.AttemptId, cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(WorkChainMutationStatus.VersionConflict, row, command.TaskId, command.AttemptId);
        }
        else if (row.TaskState != "awaiting_review" || row.AttemptState != "awaiting_review")
        {
            receipt = Rejected(WorkChainMutationStatus.InvalidState, row, command.TaskId, command.AttemptId);
        }
        else if (row.RiskTier != "low" &&
            string.Equals(row.ProducerAgentId, command.ReviewerAgentId, StringComparison.Ordinal))
        {
            receipt = Rejected(
                WorkChainMutationStatus.IndependentReviewerRequired,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.work_reviews
                    (id, tenant_id, project_id, attempt_id, reviewer_agent_id, decision, rationale, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
                """,
                cancellationToken,
                Text(command.ReviewId), Text(command.TenantId), Text(row.ProjectId), Text(command.AttemptId),
                Text(command.ReviewerAgentId), Text(command.Decision), Text(command.Rationale),
                Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_attempts SET state = $1,
                    operational_state = CASE WHEN $1 = 'rejected' THEN 'failed' ELSE 'completed' END
                WHERE id = $2 AND state = 'awaiting_review';
                """,
                cancellationToken,
                Text(command.Decision), Text(command.AttemptId));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.attempt_events (id, tenant_id, project_id, attempt_id, kind, content, occurred_at, severity)
                VALUES ($1, $2, $3, $4, 'note', $5, $6, $7);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()), Text(command.TenantId),
                Text(row.ProjectId), Text(command.AttemptId), Text(command.Rationale),
                Timestamp(command.OccurredAt),
                Text(command.Decision == "rejected" ? "error" : "info"));
            var taskState = command.Decision == "approved" ? "completed" : "ready";
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection, transaction,
                """
                UPDATE harness.work_tasks SET state = $1, version = $2, updated_at = $3,
                    board_state = CASE WHEN $1 = 'completed' THEN 'done' ELSE 'corrections' END,
                    blocked_reason = NULL
                WHERE id = $4 AND tenant_id = $5 AND version = $6;
                """,
                cancellationToken,
                Text(taskState), Bigint(nextVersion), Timestamp(command.OccurredAt), Text(command.TaskId),
                Text(command.TenantId), Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied, command.TaskId, command.AttemptId,
                nextVersion, taskState, command.Decision);
        }

        return await FinalizeMutationAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, hash,
            "gate.changed", command.OccurredAt, receipt, cancellationToken);
    }

    private async Task<WorkChainMutationReceipt> ExpireAttemptLeaseCoreAsync(
        WorkAttemptLeaseExpiredCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTaskAsync(connection, transaction, command.TaskId, cancellationToken);
        var hash = WorkChainMutationValidator.Hash(command);
        var replay = await ReadMutationInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Status = WorkChainMutationStatus.IdempotentReplay };
        }

        var row = await ReadTaskAsync(
            connection,
            transaction,
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            cancellationToken);
        WorkChainMutationReceipt receipt;
        if (row is null || row.AttemptState is null)
        {
            receipt = Rejected(WorkChainMutationStatus.NotFound, command.TaskId, command.AttemptId);
        }
        else if (row.Version != command.ExpectedTaskVersion)
        {
            receipt = Rejected(
                WorkChainMutationStatus.VersionConflict,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else if (row.TaskState != "running" || row.AttemptState != "running")
        {
            receipt = Rejected(
                WorkChainMutationStatus.InvalidState,
                row,
                command.TaskId,
                command.AttemptId);
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_attempts
                SET state='rejected',operational_state='cancelled',completed_at=$1
                WHERE id=$2 AND tenant_id=$3
                  AND state='running' AND operational_state='running';
                """,
                cancellationToken,
                Timestamp(command.OccurredAt),
                Text(command.AttemptId),
                Text(command.TenantId));
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.attempt_events
                    (id,tenant_id,project_id,attempt_id,kind,content,occurred_at,severity)
                VALUES
                    ($1,$2,$3,$4,'log','Lease expired; attempt abandoned.',$5,'warning');
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Text(row.ProjectId),
                Text(command.AttemptId),
                Timestamp(command.OccurredAt));
            var nextVersion = row.Version + 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.work_tasks
                SET state='ready',version=$1,updated_at=$2,
                    board_state='ready',blocked_reason=NULL
                WHERE id=$3 AND tenant_id=$4 AND version=$5;
                """,
                cancellationToken,
                Bigint(nextVersion),
                Timestamp(command.OccurredAt),
                Text(command.TaskId),
                Text(command.TenantId),
                Bigint(command.ExpectedTaskVersion));
            receipt = new WorkChainMutationReceipt(
                WorkChainMutationStatus.Applied,
                command.TaskId,
                command.AttemptId,
                nextVersion,
                "ready",
                "abandoned");
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            hash,
            "task.stateChanged",
            command.OccurredAt,
            receipt,
            cancellationToken);
    }

    private static async Task<TaskRow?> ReadTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string solicitationId,
        string taskId,
        string? attemptId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT t.project_id, t.version, t.state, t.risk_tier,
                   (SELECT id FROM harness.instruction_versions i WHERE i.task_id = t.id ORDER BY version DESC LIMIT 1),
                   (SELECT version FROM harness.instruction_versions i WHERE i.task_id = t.id ORDER BY version DESC LIMIT 1),
                   (SELECT COUNT(*) FROM harness.work_attempts x WHERE x.task_id = t.id),
                   (SELECT CASE WHEN operational_state='cancelled' AND state='rejected'
                                THEN 'abandoned' ELSE state END
                    FROM harness.work_attempts x WHERE x.task_id = t.id
                    ORDER BY attempt_number DESC LIMIT 1),
                   (SELECT instruction_version_id FROM harness.work_attempts x WHERE x.task_id = t.id ORDER BY attempt_number DESC LIMIT 1),
                   CASE WHEN a.operational_state='cancelled' AND a.state='rejected'
                        THEN 'abandoned' ELSE a.state END,
                   a.producer_agent_id
            FROM harness.work_tasks t
            JOIN harness.demands d ON d.id = t.demand_id
            JOIN harness.solicitations s ON s.id = d.solicitation_id
            LEFT JOIN harness.work_attempts a ON a.id = $1 AND a.task_id = t.id
            WHERE t.tenant_id = $2 AND s.id = $3 AND t.id = $4
            FOR UPDATE OF t;
            """;
        query.Parameters.Add(NullableText(attemptId));
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(solicitationId));
        query.Parameters.Add(Text(taskId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TaskRow(
                reader.GetString(0).TrimEnd(), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4).TrimEnd(), reader.GetInt32(5), checked((int)reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8).TrimEnd(),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10))
            : null;
    }

    private static async Task<WorkChainMutationReceipt?> ReadMutationInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash, response_json::text FROM harness.inbox_messages WHERE tenant_id = $1 AND idempotency_key = $2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0).TrimEnd(), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException("The idempotency key belongs to a different work-chain mutation.");
        }

        return JsonSerializer.Deserialize<WorkChainMutationReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted mutation receipt is invalid.");
    }

    private static async Task<WorkChainMutationReceipt> FinalizeMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        string hash,
        string eventType,
        DateTimeOffset occurredAt,
        WorkChainMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == WorkChainMutationStatus.Applied)
        {
            await ExecuteAsync(
                connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
                cancellationToken,
                Text($"audit-ledger:{tenantId}"));
            var payload = JsonSerializer.Serialize(new
            {
                taskId = receipt.TaskId,
                attemptId = receipt.AttemptId,
                taskState = receipt.TaskState,
                attemptState = receipt.AttemptState,
                instructionVersionId = receipt.InstructionVersionId,
                instructionVersion = receipt.InstructionVersion,
                version = receipt.TaskVersion,
            });
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection, transaction, tenantId, cancellationToken);
            var eventHash = AuditLedgerHash.Compute(
                previousHash, tenantId, sequence, eventType, payload, occurredAt);
            var outboxId = UlidValue.New(occurredAt).ToString();
            final = receipt with
            {
                LedgerSequence = sequence,
                LedgerHash = eventHash,
                OutboxMessageId = outboxId,
            };
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.audit_ledger
                    (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
                """,
                cancellationToken,
                Text(UlidValue.New(occurredAt).ToString()), Text(tenantId), Bigint(sequence),
                Text(previousHash), Text(eventHash), Text(eventType), Json(payload), Timestamp(occurredAt));
            await ExecuteAsync(
                connection, transaction,
                "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
                cancellationToken,
                Text(outboxId), Text(tenantId), Text(eventType), Json(payload), Timestamp(occurredAt));
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            Text(tenantId), Text(key), Text(hash), Json(JsonSerializer.Serialize(final)), Timestamp(occurredAt));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static Task LockTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string taskId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"work-task:{taskId}"));

    private static WorkChainMutationReceipt Rejected(
        WorkChainMutationStatus status,
        string taskId,
        string? attemptId) => new(status, taskId, attemptId, null, null, null);

    private static WorkChainMutationReceipt Rejected(
        WorkChainMutationStatus status,
        TaskRow row,
        string taskId,
        string? attemptId) => new(
            status, taskId, attemptId, row.Version, row.TaskState, row.AttemptState);

    private static NpgsqlParameter<string?> NullableText(string? value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private sealed record TaskRow(
        string ProjectId,
        long Version,
        string TaskState,
        string RiskTier,
        string LatestInstructionId,
        int LatestInstructionVersion,
        int AttemptCount,
        string? LatestAttemptState,
        string? LatestAttemptInstructionId,
        string? AttemptState,
        string? ProducerAgentId);
}
