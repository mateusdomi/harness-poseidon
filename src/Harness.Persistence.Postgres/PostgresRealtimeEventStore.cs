using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Realtime;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresRealtimeEventStore(NpgsqlDataSource dataSource) : IRealtimeEventStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<RealtimeEventAppendReceipt> AppendAsync(
        RealtimeEventAppendCommand command,
        CancellationToken cancellationToken = default)
    {
        RealtimeEventContractValidator.Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
            cancellationToken,
            Text(command.MessageId));
        var existing = await ReadByMessageIdAsync(
            connection, transaction, command.MessageId, cancellationToken);
        if (existing is not null)
        {
            EnsureReplayMatches(existing, command);
            await transaction.CommitAsync(cancellationToken);
            return ToReceipt(existing, replay: true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.realtime_streams
                (tenant_id,stream_name,last_sequence,created_at,updated_at)
            VALUES ($1,$2,0,$3,$3)
            ON CONFLICT DO NOTHING;
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.Stream),
            Timestamp(command.OccurredAt));

        string owner;
        await using (var ownership = connection.CreateCommand())
        {
            ownership.Transaction = transaction;
            ownership.CommandText =
                """
                SELECT tenant_id FROM harness.realtime_streams
                WHERE stream_name=$1 FOR UPDATE;
                """;
            ownership.Parameters.Add(Text(command.Stream));
            owner = (string)(await ownership.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Realtime stream head was not created."));
        }

        if (!string.Equals(owner.Trim(), command.TenantId, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "Realtime stream is already owned by another tenant.");
        }

        long sequence;
        await using (var advance = connection.CreateCommand())
        {
            advance.Transaction = transaction;
            advance.CommandText =
                """
                UPDATE harness.realtime_streams
                SET last_sequence=last_sequence+1,updated_at=$1
                WHERE tenant_id=$2 AND stream_name=$3
                RETURNING last_sequence;
                """;
            advance.Parameters.Add(Timestamp(command.OccurredAt));
            advance.Parameters.Add(Text(command.TenantId));
            advance.Parameters.Add(Text(command.Stream));
            sequence = (long)(await advance.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Realtime sequence was not advanced."));
        }

        var canonicalPayload = RealtimeEventContractValidator.CanonicalizePayload(command.PayloadJson);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.realtime_events
                (message_id,tenant_id,stream_name,sequence,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7);
            """,
            cancellationToken,
            Text(command.MessageId),
            Text(command.TenantId),
            Text(command.Stream),
            Bigint(sequence),
            Text(command.EventType),
            Jsonb(canonicalPayload),
            Timestamp(command.OccurredAt));
        await transaction.CommitAsync(cancellationToken);
        return new RealtimeEventAppendReceipt(
            command.MessageId,
            command.Stream,
            sequence,
            command.EventType,
            canonicalPayload,
            command.OccurredAt,
            Replay: false);
    }

    public async Task<RealtimeEventStoreSnapshot> ReadSnapshotAsync(
        string stream,
        long afterSequence,
        CancellationToken cancellationToken = default)
    {
        RealtimeEventContractValidator.ValidateRead(stream, afterSequence);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        long sequence;
        await using (var head = connection.CreateCommand())
        {
            head.CommandText =
                "SELECT last_sequence FROM harness.realtime_streams WHERE stream_name=$1;";
            head.Parameters.Add(Text(stream));
            var value = await head.ExecuteScalarAsync(cancellationToken);
            sequence = value is null ? 0 : (long)value;
        }

        var latest = new Dictionary<string, RealtimeStoredEvent>(StringComparer.Ordinal);
        var delta = new List<RealtimeStoredEvent>();
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT message_id,sequence,event_type,payload_json::text,occurred_at
            FROM harness.realtime_events
            WHERE stream_name=$1
            ORDER BY sequence;
            """;
        query.Parameters.Add(Text(stream));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new RealtimeStoredEvent(
                reader.GetString(0).Trim(),
                stream,
                reader.GetInt64(1),
                reader.GetString(2),
                RealtimeEventContractValidator.CanonicalizePayload(reader.GetString(3)),
                reader.GetFieldValue<DateTimeOffset>(4));
            latest[item.EventType] = item;
            if (item.Sequence > afterSequence)
            {
                delta.Add(item);
            }
        }

        return new RealtimeEventStoreSnapshot(stream, sequence, latest, delta);
    }

    private static async Task<RealtimeStoredEvent?> ReadByMessageIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT stream_name,sequence,event_type,payload_json::text,occurred_at
            FROM harness.realtime_events WHERE message_id=$1;
            """;
        query.Parameters.Add(Text(messageId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RealtimeStoredEvent(
                messageId,
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                RealtimeEventContractValidator.CanonicalizePayload(reader.GetString(3)),
                reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    private static void EnsureReplayMatches(
        RealtimeStoredEvent existing,
        RealtimeEventAppendCommand command)
    {
        if (!string.Equals(existing.Stream, command.Stream, StringComparison.Ordinal) ||
            !string.Equals(existing.EventType, command.EventType, StringComparison.Ordinal) ||
            !string.Equals(
                existing.PayloadJson,
                RealtimeEventContractValidator.CanonicalizePayload(command.PayloadJson),
                StringComparison.Ordinal) ||
            existing.OccurredAt != command.OccurredAt)
        {
            throw new IdempotencyConflictException(
                "Realtime message ID was already used with different content.");
        }
    }

    private static RealtimeEventAppendReceipt ToReceipt(RealtimeStoredEvent value, bool replay) =>
        new(
            value.MessageId,
            value.Stream,
            value.Sequence,
            value.EventType,
            value.PayloadJson,
            value.OccurredAt,
            replay);

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

    private static NpgsqlParameter<string> Text(string value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
}
