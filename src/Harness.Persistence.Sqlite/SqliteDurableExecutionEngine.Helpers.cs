using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDurableExecutionEngine
{
    private static async Task<DurableExecutionSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT e.tenant_id,
                   e.project_id,
                   e.id,
                   e.state,
                   e.payload_json,
                   e.attempt_count,
                   e.max_attempts,
                   e.available_at,
                   e.active_attempt_id,
                   a.owner,
                   e.fencing_token,
                   a.lease_expires_at,
                   a.last_heartbeat_at,
                   (SELECT checkpoint_key
                    FROM durable_checkpoints c
                    WHERE c.execution_id = e.id
                    ORDER BY c.created_at DESC, c.checkpoint_key DESC
                    LIMIT 1),
                   (SELECT payload_json
                    FROM durable_checkpoints c
                    WHERE c.execution_id = e.id
                    ORDER BY c.created_at DESC, c.checkpoint_key DESC
                    LIMIT 1),
                   e.last_error,
                   e.version
            FROM durable_executions e
            LEFT JOIN durable_attempts a ON a.id = e.active_attempt_id
            WHERE e.tenant_id = $tenantId AND e.id = $executionId;
            """;
        Add(command, "$tenantId", tenantId);
        Add(command, "$executionId", executionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DurableExecutionSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DurableExecutionStateCodec.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            ParseStorage(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : ParseStorage(reader.GetString(11)),
            reader.IsDBNull(12) ? null : ParseStorage(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetInt64(16));
    }

    private static async Task<DurableCommandResult?> ReadInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT command_hash, response_json
            FROM durable_command_inbox
            WHERE tenant_id = $tenantId AND idempotency_key = $idempotencyKey;
            """;
        Add(command, "$tenantId", tenantId);
        Add(command, "$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), commandHash, StringComparison.Ordinal))
        {
            return new DurableCommandResult(DurableCommandStatus.IdempotencyConflict);
        }

        var persisted = JsonSerializer.Deserialize<DurableCommandResult>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted durable command result is invalid.");
        return persisted with { Status = DurableCommandStatus.IdempotentReplay };
    }

    private static async Task WriteInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        DurableCommandResult result,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO durable_command_inbox
                (tenant_id, idempotency_key, command_hash, response_json, processed_at)
            VALUES
                ($tenantId, $idempotencyKey, $commandHash, $responseJson, $processedAt);
            """;
        Add(command, "$tenantId", tenantId);
        Add(command, "$idempotencyKey", idempotencyKey);
        Add(command, "$commandHash", commandHash);
        Add(command, "$responseJson", JsonSerializer.Serialize(result));
        Add(command, "$processedAt", ToStorage(processedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AppendTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string executionId,
        DurableExecutionState? from,
        DurableExecutionState to,
        string reason,
        string? attemptId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        long sequence;
        await using (var sequenceCommand = connection.CreateCommand())
        {
            sequenceCommand.Transaction = transaction;
            sequenceCommand.CommandText =
                "SELECT COALESCE(MAX(sequence), 0) + 1 FROM durable_transitions WHERE execution_id = $executionId;";
            Add(sequenceCommand, "$executionId", executionId);
            sequence = Convert.ToInt64(
                await sequenceCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        var payload = JsonSerializer.Serialize(new
        {
            executionId,
            attemptId,
            state = DurableExecutionStateCodec.ToStorage(to),
            reason,
        });
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO durable_transitions
                (execution_id, sequence, from_state, to_state, reason, attempt_id, occurred_at)
            VALUES
                ($executionId, $sequence, $fromState, $toState, $reason, $attemptId, $occurredAt);
            INSERT INTO durable_execution_outbox
                (execution_id, transition_sequence, event_type, payload_json, occurred_at)
            VALUES
                ($executionId, $sequence, $eventType, $payloadJson, $occurredAt);
            """;
        Add(command, "$executionId", executionId);
        Add(command, "$sequence", sequence);
        Add(command, "$fromState", from is null ? DBNull.Value : DurableExecutionStateCodec.ToStorage(from.Value));
        Add(command, "$toState", DurableExecutionStateCodec.ToStorage(to));
        Add(command, "$reason", reason);
        Add(command, "$attemptId", attemptId is null ? DBNull.Value : attemptId);
        Add(command, "$occurredAt", ToStorage(occurredAt));
        Add(command, "$eventType", "durable.stateChanged");
        Add(command, "$payloadJson", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AppendAuditLedgerAsync(
            connection,
            transaction,
            executionId,
            payload,
            occurredAt,
            cancellationToken);
    }

    private static async Task AppendAuditLedgerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string executionId,
        string payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        payloadJson = PersistenceSanitizer.SanitizeJson(payloadJson);
        string tenantId;
        long sequence;
        string previousHash;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText =
                """
                SELECT e.tenant_id,
                       COALESCE((SELECT MAX(l.sequence) FROM audit_ledger l WHERE l.tenant_id = e.tenant_id), 0),
                       COALESCE((SELECT l.event_hash FROM audit_ledger l
                                 WHERE l.tenant_id = e.tenant_id ORDER BY l.sequence DESC LIMIT 1), $genesis)
                FROM durable_executions e
                WHERE e.id = $executionId;
                """;
            Add(state, "$executionId", executionId);
            Add(state, "$genesis", AuditLedgerHash.Genesis);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The durable execution tenant could not be resolved for auditing.");
            }

            tenantId = reader.GetString(0);
            sequence = reader.GetInt64(1) + 1;
            previousHash = reader.GetString(2);
        }

        const string eventType = "durable.stateChanged";
        var eventHash = AuditLedgerHash.Compute(
            previousHash,
            tenantId,
            sequence,
            eventType,
            payloadJson,
            occurredAt);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES
                ($id, $tenantId, $sequence, $previousHash, $eventHash, $eventType, $payloadJson, $occurredAt);
            """;
        Add(insert, "$id", UlidValue.New(occurredAt).ToString());
        Add(insert, "$tenantId", tenantId);
        Add(insert, "$sequence", sequence);
        Add(insert, "$previousHash", previousHash);
        Add(insert, "$eventHash", eventHash);
        Add(insert, "$eventType", eventType);
        Add(insert, "$payloadJson", payloadJson);
        Add(insert, "$occurredAt", ToStorage(occurredAt));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(string? Key, string? Payload)> ReadLatestCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT checkpoint_key, payload_json
            FROM durable_checkpoints
            WHERE execution_id = $executionId
            ORDER BY created_at DESC, checkpoint_key DESC
            LIMIT 1;
            """;
        Add(command, "$executionId", executionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : (null, null);
    }

    private static long ToMilliseconds(TimeSpan value) => checked(value.Ticks / TimeSpan.TicksPerMillisecond);

    private static string ToStorage(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStorage(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
