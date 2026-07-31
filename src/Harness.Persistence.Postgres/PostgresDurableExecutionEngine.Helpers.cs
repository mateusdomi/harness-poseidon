using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresDurableExecutionEngine
{
    private async Task<DurableExecutionSnapshot?> GetCoreAsync(
        string tenantId,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadSnapshotAsync(connection, null, tenantId, executionId, cancellationToken);
    }

    private static async Task<DurableExecutionSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT e.tenant_id, e.project_id, e.id, e.state, e.payload_json::text,
                   e.attempt_count, e.max_attempts, e.available_at, e.active_attempt_id,
                   a.owner, e.fencing_token, a.lease_expires_at, a.last_heartbeat_at,
                   (SELECT checkpoint_key FROM harness.durable_checkpoints c
                    WHERE c.execution_id = e.id ORDER BY c.created_at DESC, c.checkpoint_key DESC LIMIT 1),
                   (SELECT payload_json::text FROM harness.durable_checkpoints c
                    WHERE c.execution_id = e.id ORDER BY c.created_at DESC, c.checkpoint_key DESC LIMIT 1),
                   e.last_error, e.version
            FROM harness.durable_executions e
            LEFT JOIN harness.durable_attempts a ON a.id = e.active_attempt_id
            WHERE e.tenant_id = $1 AND e.id = $2
            """ + (transaction is null ? ";" : " FOR UPDATE OF e;");
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(executionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DurableExecutionSnapshot(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            DurableExecutionStateCodec.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetString(8).TrimEnd(),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetInt64(16));
    }

    private static async Task<DurableCommandResult?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT command_hash, response_json::text
            FROM harness.durable_command_inbox
            WHERE tenant_id = $1 AND idempotency_key = $2;
            """;
        command.Parameters.Add(Text(tenantId));
        command.Parameters.Add(Text(idempotencyKey));
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

    private static Task WriteInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        DurableCommandResult result,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.durable_command_inbox
                (tenant_id, idempotency_key, command_hash, response_json, processed_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            Text(tenantId),
            Text(idempotencyKey),
            Text(commandHash),
            Json(JsonSerializer.Serialize(result)),
            Timestamp(processedAt));

    private static async Task AppendTransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
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
                "SELECT COALESCE(MAX(sequence), 0) + 1 FROM harness.durable_transitions WHERE execution_id = $1;";
            sequenceCommand.Parameters.Add(Text(executionId));
            sequence = Convert.ToInt64(
                await sequenceCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.durable_transitions
                (execution_id, sequence, from_state, to_state, reason, attempt_id, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """,
            cancellationToken,
            Text(executionId),
            Bigint(sequence),
            NullableText(from is null ? null : DurableExecutionStateCodec.ToStorage(from.Value)),
            Text(DurableExecutionStateCodec.ToStorage(to)),
            Text(reason),
            NullableText(attemptId),
            Timestamp(occurredAt));
        var payload = JsonSerializer.Serialize(new
        {
            executionId,
            attemptId,
            state = DurableExecutionStateCodec.ToStorage(to),
            reason,
        });
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.durable_execution_outbox
                (execution_id, transition_sequence, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            Text(executionId),
            Bigint(sequence),
            Text("durable.stateChanged"),
            Json(payload),
            Timestamp(occurredAt));
        await AppendAuditLedgerAsync(
            connection,
            transaction,
            executionId,
            payload,
            occurredAt,
            cancellationToken);
    }

    private static async Task AppendAuditLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string executionId,
        string payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        payloadJson = PersistenceSanitizer.SanitizeJson(payloadJson);
        string tenantId;
        await using (var tenant = connection.CreateCommand())
        {
            tenant.Transaction = transaction;
            tenant.CommandText = "SELECT tenant_id FROM harness.durable_executions WHERE id = $1;";
            tenant.Parameters.Add(Text(executionId));
            tenantId = ((string?)await tenant.ExecuteScalarAsync(cancellationToken))?.TrimEnd()
                ?? throw new InvalidOperationException(
                    "The durable execution tenant could not be resolved for auditing.");
        }

        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenantId}"));
        long sequence = 1;
        var previousHash = AuditLedgerHash.Genesis;
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText =
                """
                SELECT sequence, event_hash
                FROM harness.audit_ledger
                WHERE tenant_id = $1
                ORDER BY sequence DESC
                LIMIT 1
                FOR UPDATE;
                """;
            previous.Parameters.Add(Text(tenantId));
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                sequence = reader.GetInt64(0) + 1;
                previousHash = reader.GetString(1).TrimEnd();
            }
        }

        const string eventType = "durable.stateChanged";
        var eventHash = AuditLedgerHash.Compute(
            previousHash,
            tenantId,
            sequence,
            eventType,
            payloadJson,
            occurredAt);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Bigint(sequence),
            Text(previousHash),
            Text(eventHash),
            Text(eventType),
            Json(payloadJson),
            Timestamp(occurredAt));
    }

    private static async Task<(string? Key, string? Payload)> ReadLatestCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT checkpoint_key, payload_json::text
            FROM harness.durable_checkpoints
            WHERE execution_id = $1
            ORDER BY created_at DESC, checkpoint_key DESC
            LIMIT 1;
            """;
        command.Parameters.Add(Text(executionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : (null, null);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static long ToMilliseconds(TimeSpan value) => checked(value.Ticks / TimeSpan.TicksPerMillisecond);

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<string?> NullableText(string? value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<decimal> Numeric(decimal value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) => new()
    {
        TypedValue = value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
