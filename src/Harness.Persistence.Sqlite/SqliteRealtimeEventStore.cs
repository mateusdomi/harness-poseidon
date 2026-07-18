using System.Globalization;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Realtime;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteRealtimeEventStore(SqliteWriteDispatcher dispatcher) : IRealtimeEventStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<RealtimeEventAppendReceipt> AppendAsync(
        RealtimeEventAppendCommand command,
        CancellationToken cancellationToken = default)
    {
        RealtimeEventContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => AppendCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<RealtimeEventStoreSnapshot> ReadSnapshotAsync(
        string stream,
        long afterSequence,
        CancellationToken cancellationToken = default)
    {
        RealtimeEventContractValidator.ValidateRead(stream, afterSequence);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadCoreAsync(connection, stream, afterSequence, token),
            cancellationToken);
    }

    private static async Task<RealtimeEventAppendReceipt> AppendCoreAsync(
        SqliteConnection connection,
        RealtimeEventAppendCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var existing = await ReadByMessageIdAsync(
            connection, transaction, command.MessageId, cancellationToken);
        if (existing is not null)
        {
            EnsureReplayMatches(existing, command);
            await transaction.CommitAsync(cancellationToken);
            return ToReceipt(existing, replay: true);
        }

        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText =
                """
                INSERT OR IGNORE INTO realtime_streams
                    (tenant_id,stream_name,last_sequence,created_at,updated_at)
                VALUES ($tenantId,$stream,0,$occurredAt,$occurredAt);
                """;
            Add(create, "$tenantId", command.TenantId);
            Add(create, "$stream", command.Stream);
            Add(create, "$occurredAt", Store(command.OccurredAt));
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var ownership = connection.CreateCommand())
        {
            ownership.Transaction = transaction;
            ownership.CommandText =
                "SELECT tenant_id FROM realtime_streams WHERE stream_name=$stream;";
            Add(ownership, "$stream", command.Stream);
            var tenant = Convert.ToString(
                await ownership.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (!string.Equals(tenant, command.TenantId, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException(
                    "Realtime stream is already owned by another tenant.");
            }
        }

        long sequence;
        await using (var advance = connection.CreateCommand())
        {
            advance.Transaction = transaction;
            advance.CommandText =
                """
                UPDATE realtime_streams
                SET last_sequence=last_sequence+1,updated_at=$occurredAt
                WHERE tenant_id=$tenantId AND stream_name=$stream
                RETURNING last_sequence;
                """;
            Add(advance, "$tenantId", command.TenantId);
            Add(advance, "$stream", command.Stream);
            Add(advance, "$occurredAt", Store(command.OccurredAt));
            sequence = Convert.ToInt64(
                await advance.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        var canonicalPayload = RealtimeEventContractValidator.CanonicalizePayload(command.PayloadJson);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO realtime_events
                    (message_id,tenant_id,stream_name,sequence,event_type,payload_json,occurred_at)
                VALUES ($messageId,$tenantId,$stream,$sequence,$eventType,$payload,$occurredAt);
                """;
            Add(insert, "$messageId", command.MessageId);
            Add(insert, "$tenantId", command.TenantId);
            Add(insert, "$stream", command.Stream);
            Add(insert, "$sequence", sequence);
            Add(insert, "$eventType", command.EventType);
            Add(insert, "$payload", canonicalPayload);
            Add(insert, "$occurredAt", Store(command.OccurredAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

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

    private static async Task<RealtimeEventStoreSnapshot> ReadCoreAsync(
        SqliteConnection connection,
        string stream,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        long sequence;
        await using (var head = connection.CreateCommand())
        {
            head.CommandText =
                "SELECT last_sequence FROM realtime_streams WHERE stream_name=$stream;";
            Add(head, "$stream", stream);
            var value = await head.ExecuteScalarAsync(cancellationToken);
            sequence = value is null
                ? 0
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        var latest = new Dictionary<string, RealtimeStoredEvent>(StringComparer.Ordinal);
        var delta = new List<RealtimeStoredEvent>();
        await using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT message_id,sequence,event_type,payload_json,occurred_at
            FROM realtime_events
            WHERE stream_name=$stream
            ORDER BY sequence;
            """;
        Add(query, "$stream", stream);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new RealtimeStoredEvent(
                reader.GetString(0),
                stream,
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
            latest[item.EventType] = item;
            if (item.Sequence > afterSequence)
            {
                delta.Add(item);
            }
        }

        return new RealtimeEventStoreSnapshot(stream, sequence, latest, delta);
    }

    private static async Task<RealtimeStoredEvent?> ReadByMessageIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT stream_name,sequence,event_type,payload_json,occurred_at
            FROM realtime_events WHERE message_id=$messageId;
            """;
        Add(query, "$messageId", messageId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RealtimeStoredEvent(
                messageId,
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture))
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

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
