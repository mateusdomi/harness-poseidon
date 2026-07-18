using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteWorkflowStore
{
    public Task<WorkflowRunMutationReceipt> TransitionRunAsync(
        WorkflowRunTransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        WorkflowRunMutationValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => TransitionRunCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<WorkflowRunMutationReceipt> TransitionRunCoreAsync(
        SqliteConnection connection,
        WorkflowRunTransitionCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT version,state FROM workflow_runs WHERE tenant_id=$tenantId AND id=$runId;";
        Add(query, "$tenantId", tenantId);
        Add(query, "$runId", runId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RunMutationRow(reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async Task UpdateRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowRunTransitionCommand command,
        string nextState,
        long nextVersion,
        CancellationToken cancellationToken)
    {
        var startedAt = command.Transition == WorkflowRunTransition.Start
            ? Store(command.OccurredAt)
            : null;
        var completedAt = command.Transition == WorkflowRunTransition.Cancel
            ? Store(command.OccurredAt)
            : null;
        await using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText =
            """
            UPDATE workflow_runs
            SET state=$state,version=$nextVersion,
                started_at=COALESCE($startedAt,started_at),
                completed_at=COALESCE($completedAt,completed_at)
            WHERE tenant_id=$tenantId AND id=$runId AND version=$expectedVersion;
            """;
        Add(mutation, "$state", nextState);
        Add(mutation, "$nextVersion", nextVersion);
        mutation.Parameters.AddWithValue("$startedAt", (object?)startedAt ?? DBNull.Value);
        mutation.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
        Add(mutation, "$tenantId", command.TenantId);
        Add(mutation, "$runId", command.RunId);
        Add(mutation, "$expectedVersion", command.ExpectedRunVersion);
        if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The workflow run changed during serialized mutation.");
        }
    }

    private static async Task ActivateFirstPhaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var mutation = connection.CreateCommand();
        mutation.Transaction = transaction;
        mutation.CommandText =
            """
            UPDATE workflow_phase_runs
            SET state='active',version=version+1,activated_at=$occurredAt
            WHERE id=(SELECT id FROM workflow_phase_runs
                      WHERE workflow_run_id=$runId AND state='pending'
                      ORDER BY phase_order LIMIT 1);
            """;
        Add(mutation, "$occurredAt", Store(occurredAt));
        Add(mutation, "$runId", runId);
        if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("A pending workflow phase is required to start a run.");
        }
    }

    private static async Task<WorkflowRunMutationReceipt?> ReadMutationInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string key,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash,response_json FROM inbox_messages WHERE tenant_id=$tenantId AND idempotency_key=$key;";
        Add(query, "$tenantId", tenantId);
        Add(query, "$key", key);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different workflow run mutation.");
        }

        return JsonSerializer.Deserialize<WorkflowRunMutationReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted workflow mutation receipt is invalid.");
    }

    private static async Task<WorkflowRunMutationReceipt> FinalizeMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowRunTransitionCommand command,
        string hash,
        WorkflowRunMutationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var final = receipt;
        if (receipt.Status == WorkflowRunMutationStatus.Applied)
        {
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
                INSERT INTO audit_ledger
                    (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
                VALUES ($id,$tenantId,$sequence,$previousHash,$eventHash,$eventType,$payload,$occurredAt);
                INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at)
                VALUES ($outboxId,$tenantId,$eventType,$payload,$occurredAt);
                """,
                cancellationToken,
                ("$id", UlidValue.New(command.OccurredAt).ToString()),
                ("$tenantId", command.TenantId),
                ("$sequence", sequence),
                ("$previousHash", previousHash),
                ("$eventHash", eventHash),
                ("$eventType", eventType),
                ("$payload", payload),
                ("$occurredAt", Store(command.OccurredAt)),
                ("$outboxId", outboxId));
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO inbox_messages
                (tenant_id,idempotency_key,message_hash,response_json,processed_at)
            VALUES ($tenantId,$key,$hash,$response,$occurredAt);
            """,
            cancellationToken,
            ("$tenantId", command.TenantId),
            ("$key", command.IdempotencyKey),
            ("$hash", hash),
            ("$response", JsonSerializer.Serialize(final)),
            ("$occurredAt", Store(command.OccurredAt)));
        await transaction.CommitAsync(cancellationToken);
        return final;
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
