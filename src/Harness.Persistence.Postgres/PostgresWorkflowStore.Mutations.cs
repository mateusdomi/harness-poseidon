using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkflowStore
{
    public Task<WorkflowRunMutationReceipt> TransitionRunAsync(
        WorkflowRunTransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return TransitionRunCoreAsync(command, cancellationToken);
    }

    private async Task<WorkflowRunMutationReceipt> TransitionRunCoreAsync(
        WorkflowRunTransitionCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockMutationAsync(connection, transaction, command, cancellationToken);
        var hash = WorkflowRunMutationValidator.Hash(command);
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
            return replay with { Status = WorkflowRunMutationStatus.IdempotentReplay };
        }

        var row = await ReadRunForMutationAsync(
            connection, transaction, command.TenantId, command.RunId, cancellationToken);
        WorkflowRunMutationReceipt receipt;
        if (row is null)
        {
            receipt = Rejected(WorkflowRunMutationStatus.NotFound, command.RunId);
        }
        else if (row.Version != command.ExpectedRunVersion)
        {
            receipt = Rejected(
                WorkflowRunMutationStatus.VersionConflict,
                command.RunId,
                row.Version,
                row.State);
        }
        else if (!CanTransition(row.State, command.Transition))
        {
            receipt = Rejected(
                WorkflowRunMutationStatus.InvalidState,
                command.RunId,
                row.Version,
                row.State);
        }
        else
        {
            var nextState = TargetState(command.Transition);
            var nextVersion = row.Version + 1;
            await UpdateRunAsync(
                connection,
                transaction,
                command,
                nextState,
                nextVersion,
                cancellationToken);
            if (command.Transition == WorkflowRunTransition.Start)
            {
                await ActivateFirstPhaseAsync(
                    connection, transaction, command.RunId, command.OccurredAt, cancellationToken);
            }

            receipt = new WorkflowRunMutationReceipt(
                WorkflowRunMutationStatus.Applied,
                command.RunId,
                nextVersion,
                nextState);
        }

        return await FinalizeMutationAsync(
            connection,
            transaction,
            command,
            hash,
            receipt,
            cancellationToken);
    }

    private static async Task<RunMutationRow?> ReadRunForMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT version,state FROM harness.workflow_runs WHERE tenant_id=$1 AND id=$2 FOR UPDATE;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(runId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RunMutationRow(reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async Task UpdateRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkflowRunTransitionCommand command,
        string nextState,
        long nextVersion,
        CancellationToken cancellationToken)
    {
        var sql = command.Transition switch
        {
            WorkflowRunTransition.Start =>
                "UPDATE harness.workflow_runs SET state=$1,version=$2,started_at=$3 WHERE tenant_id=$4 AND id=$5 AND version=$6;",
            WorkflowRunTransition.Cancel =>
                "UPDATE harness.workflow_runs SET state=$1,version=$2,completed_at=$3 WHERE tenant_id=$4 AND id=$5 AND version=$6;",
            _ =>
                "UPDATE harness.workflow_runs SET state=$1,version=$2 WHERE tenant_id=$3 AND id=$4 AND version=$5;",
        };
        await using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText = sql;
        mutation.Parameters.Add(Text(nextState));
        mutation.Parameters.Add(Bigint(nextVersion));
        if (command.Transition is WorkflowRunTransition.Start or WorkflowRunTransition.Cancel)
        {
            mutation.Parameters.Add(Timestamp(command.OccurredAt));
        }

        mutation.Parameters.Add(Text(command.TenantId));
        mutation.Parameters.Add(Text(command.RunId));
        mutation.Parameters.Add(Bigint(command.ExpectedRunVersion));
        if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The workflow run changed while its mutation lock was held.");
        }
    }

    private static async Task ActivateFirstPhaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText =
            """
            UPDATE harness.workflow_phase_runs
            SET state='active',version=version+1,activated_at=$1
            WHERE id=(SELECT id FROM harness.workflow_phase_runs
                      WHERE workflow_run_id=$2 AND state='pending'
                      ORDER BY phase_order LIMIT 1);
            """;
        mutation.Parameters.Add(Timestamp(occurredAt));
        mutation.Parameters.Add(Text(runId));
        if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("A pending workflow phase is required to start a run.");
        }
    }

    private static async Task<WorkflowRunMutationReceipt?> ReadMutationInboxAsync(
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
            "SELECT message_hash,response_json::text FROM harness.inbox_messages WHERE tenant_id=$1 AND idempotency_key=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(Trim(reader.GetString(0)), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different workflow run mutation.");
        }

        return JsonSerializer.Deserialize<WorkflowRunMutationReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted workflow mutation receipt is invalid.");
    }

    private static async Task<WorkflowRunMutationReceipt> FinalizeMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkflowRunTransitionCommand command,
        string hash,
        WorkflowRunMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == WorkflowRunMutationStatus.Applied)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
                cancellationToken,
                Text($"audit-ledger:{command.TenantId}"));
            var payload = JsonSerializer.Serialize(new
            {
                runId = receipt.RunId,
                state = receipt.RunState,
                version = receipt.RunVersion,
                transition = command.Transition.ToString().ToLowerInvariant(),
            });
            var (sequence, previousHash) = await ReadLedgerTailAsync(
                connection, transaction, command.TenantId, cancellationToken);
            const string eventType = "progress.updated";
            var eventHash = AuditLedgerHash.Compute(
                previousHash,
                command.TenantId,
                sequence,
                eventType,
                payload,
                command.OccurredAt);
            var outboxId = UlidValue.New(command.OccurredAt).ToString();
            final = receipt with
            {
                LedgerSequence = sequence,
                LedgerHash = eventHash,
                OutboxMessageId = outboxId,
            };
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.audit_ledger
                    (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                """,
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Bigint(sequence),
                Text(previousHash),
                Text(eventHash),
                Text(eventType),
                Json(payload),
                Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO harness.outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($1,$2,$3,$4,$5);",
                cancellationToken,
                Text(outboxId),
                Text(command.TenantId),
                Text(eventType),
                Json(payload),
                Timestamp(command.OccurredAt));
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($1,$2,$3,$4,$5);
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.IdempotencyKey),
            Text(hash),
            Json(JsonSerializer.Serialize(final)),
            Timestamp(command.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    private static async Task LockMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkflowRunTransitionCommand command,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"workflow-mutation:{command.TenantId}:{command.IdempotencyKey}"));
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text($"workflow-run:{command.RunId}"));
    }

    private static bool CanTransition(string state, WorkflowRunTransition transition) =>
        transition switch
        {
            WorkflowRunTransition.Start => state == "pending",
            WorkflowRunTransition.Pause => state == "running",
            WorkflowRunTransition.Resume => state == "paused",
            WorkflowRunTransition.Cancel => state is not ("completed" or "cancelled"),
            _ => false,
        };

    private static string TargetState(WorkflowRunTransition transition) =>
        transition switch
        {
            WorkflowRunTransition.Start or WorkflowRunTransition.Resume => "running",
            WorkflowRunTransition.Pause => "paused",
            WorkflowRunTransition.Cancel => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    private static WorkflowRunMutationReceipt Rejected(
        WorkflowRunMutationStatus status,
        string runId,
        long? version = null,
        string? state = null) => new(status, runId, version, state);

    private sealed record RunMutationRow(long Version, string State);
}
