using Harness.Persistence.Abstractions.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresOutboxStore(NpgsqlDataSource dataSource) : IOutboxStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<OutboxLease?> TryAcquireNextAsync(
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        OutboxContractValidator.ValidateAcquire(owner, leaseDuration, now);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH candidate AS
            (
                SELECT id
                FROM harness.outbox_messages
                WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
                  AND COALESCE(available_at,occurred_at) <= $1
                  AND (lock_owner IS NULL OR lock_expires_at <= $1)
                ORDER BY COALESCE(available_at,occurred_at),occurred_at,id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE harness.outbox_messages message
            SET lock_owner=$2,lock_token=message.lock_token+1,lock_expires_at=$3
            FROM candidate
            WHERE message.id=candidate.id
            RETURNING message.id,message.tenant_id,message.event_type,message.payload_json::text,
                      message.occurred_at,message.attempts,message.lock_owner,
                      message.lock_token,message.lock_expires_at;
            """;
        command.Parameters.Add(Timestamp(now));
        command.Parameters.Add(Text(owner));
        command.Parameters.Add(Timestamp(now.Add(leaseDuration)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        OutboxLease? lease = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.GetString(3);
            OutboxContractValidator.ValidatePayload(payload, nameof(owner));
            lease = new OutboxLease(
                Trim(reader.GetString(0)),
                Trim(reader.GetString(1)),
                reader.GetString(2),
                payload,
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetFieldValue<DateTimeOffset>(8));
        }

        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    public async Task<OutboxMutationResult> MarkDispatchedAsync(
        OutboxDispatchCommand command,
        CancellationToken cancellationToken = default)
    {
        OutboxContractValidator.Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadMutationRowAsync(
            connection,
            transaction,
            command.MessageId,
            cancellationToken);
        var result = TerminalOrLeaseRejection(
            row,
            command.MessageId,
            command.Owner,
            command.FencingToken,
            command.DispatchedAt);
        if (result is null)
        {
            var changed = await ExecuteCountAsync(
                connection,
                transaction,
                """
                UPDATE harness.outbox_messages
                SET dispatched_at=$1,lock_owner=NULL,lock_expires_at=NULL,last_error=NULL
                WHERE id=$2 AND lock_owner=$3 AND lock_token=$4
                  AND lock_expires_at > $1
                  AND dispatched_at IS NULL AND dead_lettered_at IS NULL;
                """,
                cancellationToken,
                Timestamp(command.DispatchedAt),
                Text(command.MessageId),
                Text(command.Owner),
                Bigint(command.FencingToken));
            if (changed != 1)
            {
                throw new InvalidOperationException("Validated outbox completion was not applied.");
            }

            result = new OutboxMutationResult(
                OutboxMutationStatus.Applied,
                command.MessageId,
                row!.Attempts);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<OutboxMutationResult> RecordFailureAsync(
        OutboxFailureCommand command,
        CancellationToken cancellationToken = default)
    {
        OutboxContractValidator.Validate(command);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadMutationRowAsync(
            connection,
            transaction,
            command.MessageId,
            cancellationToken);
        var result = TerminalOrLeaseRejection(
            row,
            command.MessageId,
            command.Owner,
            command.FencingToken,
            command.FailedAt);
        if (result is null)
        {
            var attempt = row!.Attempts + 1;
            var deadLettered = attempt >= command.RetryPolicy.MaximumAttempts;
            var availableAt = deadLettered
                ? (DateTimeOffset?)null
                : command.FailedAt.Add(command.RetryPolicy.DelayAfterFailure(attempt));
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.outbox_dispatch_failures
                    (id,message_id,attempt,error,dead_lettered,occurred_at)
                VALUES ($1,$2,$3,$4,$5,$6);
                """,
                cancellationToken,
                Text(command.FailureId),
                Text(command.MessageId),
                Integer(attempt),
                Text(command.Error),
                Boolean(deadLettered),
                Timestamp(command.FailedAt));
            var changed = await ExecuteCountAsync(
                connection,
                transaction,
                """
                UPDATE harness.outbox_messages
                SET attempts=$1,last_error=$2,available_at=$3,dead_lettered_at=$4,
                    lock_owner=NULL,lock_expires_at=NULL
                WHERE id=$5 AND lock_owner=$6 AND lock_token=$7
                  AND lock_expires_at > $8
                  AND dispatched_at IS NULL AND dead_lettered_at IS NULL;
                """,
                cancellationToken,
                Integer(attempt),
                Text(command.Error),
                NullableTimestamp(availableAt),
                NullableTimestamp(deadLettered ? command.FailedAt : null),
                Text(command.MessageId),
                Text(command.Owner),
                Bigint(command.FencingToken),
                Timestamp(command.FailedAt));
            if (changed != 1)
            {
                throw new InvalidOperationException("Outbox failure was not recorded atomically.");
            }

            result = new OutboxMutationResult(
                deadLettered ? OutboxMutationStatus.DeadLettered : OutboxMutationStatus.Applied,
                command.MessageId,
                attempt,
                availableAt);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<int> ReleaseExpiredClaimsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (now == default)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Timestamp is required.");
        }

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE harness.outbox_messages
            SET lock_owner=NULL,lock_expires_at=NULL
            WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
              AND lock_owner IS NOT NULL AND lock_expires_at <= $1;
            """);
        command.Parameters.Add(Timestamp(now));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OutboxStoreSnapshot> ReadSnapshotAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var snapshotNow = now ?? DateTimeOffset.UtcNow;
        var sinceMinute = snapshotNow.AddMinutes(-1);
        var sinceHour = snapshotNow.AddHours(-1);
        await using var query = _dataSource.CreateCommand(
            """
            SELECT
                COUNT(*) FILTER (WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
                                       AND lock_owner IS NULL),
                COUNT(*) FILTER (WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
                                       AND lock_owner IS NOT NULL),
                COUNT(*) FILTER (WHERE dispatched_at IS NOT NULL),
                COUNT(*) FILTER (WHERE dead_lettered_at IS NOT NULL),
                (SELECT COUNT(*) FROM harness.outbox_dispatch_failures),
                MIN(occurred_at) FILTER (WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
                                               AND lock_owner IS NULL),
                COUNT(*) FILTER (WHERE dispatched_at IS NOT NULL AND dispatched_at >= $1),
                COUNT(*) FILTER (WHERE dispatched_at IS NOT NULL AND dispatched_at >= $2)
            FROM harness.outbox_messages;
            """);
        query.Parameters.Add(Timestamp(sinceMinute));
        query.Parameters.Add(Timestamp(sinceHour));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Outbox snapshot query returned no row.");
        }

        var oldestPendingAt = reader.IsDBNull(5)
            ? (DateTimeOffset?)null
            : reader.GetFieldValue<DateTimeOffset>(5);
        return new OutboxStoreSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4))
        {
            OldestPendingAge = oldestPendingAt is not null ? snapshotNow - oldestPendingAt.Value : null,
            DispatchedLastMinute = reader.GetInt64(6),
            DispatchedLastHour = reader.GetInt64(7),
        };
    }

    private static async Task<OutboxMutationRow?> ReadMutationRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT attempts,lock_owner,lock_token,lock_expires_at,dispatched_at,dead_lettered_at
            FROM harness.outbox_messages WHERE id=$1
            FOR UPDATE;
            """;
        query.Parameters.Add(Text(messageId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OutboxMutationRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                !reader.IsDBNull(4),
                !reader.IsDBNull(5))
            : null;
    }

    private static OutboxMutationResult? TerminalOrLeaseRejection(
        OutboxMutationRow? row,
        string messageId,
        string owner,
        long fencingToken,
        DateTimeOffset occurredAt)
    {
        if (row is null)
        {
            return new OutboxMutationResult(OutboxMutationStatus.NotFound, messageId);
        }

        if (row.Dispatched || row.DeadLettered)
        {
            return new OutboxMutationResult(
                OutboxMutationStatus.AlreadyTerminal,
                messageId,
                row.Attempts);
        }

        if (!string.Equals(row.LockOwner, owner, StringComparison.Ordinal) ||
            row.LockToken != fencingToken ||
            row.LockExpiresAt is null ||
            row.LockExpiresAt <= occurredAt)
        {
            return new OutboxMutationResult(
                OutboxMutationStatus.LeaseRejected,
                messageId,
                row.Attempts);
        }

        return null;
    }

    private static async Task<int> ExecuteCountAsync(
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
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters) =>
        _ = await ExecuteCountAsync(
            connection,
            transaction,
            sql,
            cancellationToken,
            parameters);

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<bool> Boolean(bool value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableTimestamp(DateTimeOffset? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.TimestampTz,
        Value = (object?)value ?? DBNull.Value,
    };

    private static string Trim(string value) => value.TrimEnd();

    private sealed record OutboxMutationRow(
        int Attempts,
        string? LockOwner,
        long LockToken,
        DateTimeOffset? LockExpiresAt,
        bool Dispatched,
        bool DeadLettered);
}
