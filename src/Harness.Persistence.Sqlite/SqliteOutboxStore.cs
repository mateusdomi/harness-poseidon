using System.Globalization;
using Harness.Persistence.Abstractions.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteOutboxStore(SqliteWriteDispatcher dispatcher) : IOutboxStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<OutboxLease?> TryAcquireNextAsync(
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        OutboxContractValidator.ValidateAcquire(owner, leaseDuration, now);
        return _dispatcher.ExecuteAsync(
            (connection, token) => AcquireCoreAsync(
                connection,
                owner,
                leaseDuration,
                now,
                token),
            cancellationToken);
    }

    public Task<OutboxMutationResult> MarkDispatchedAsync(
        OutboxDispatchCommand command,
        CancellationToken cancellationToken = default)
    {
        OutboxContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => MarkDispatchedCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<OutboxMutationResult> RecordFailureAsync(
        OutboxFailureCommand command,
        CancellationToken cancellationToken = default)
    {
        OutboxContractValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => RecordFailureCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<int> ReleaseExpiredClaimsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (now == default)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Timestamp is required.");
        }

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE outbox_messages
                    SET lock_owner=NULL,lock_expires_at=NULL
                    WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
                      AND lock_owner IS NOT NULL AND lock_expires_at <= $now;
                    """;
                Add(command, "$now", Store(now));
                return await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    public Task<OutboxStoreSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    """
                    SELECT
                        SUM(CASE WHEN dispatched_at IS NULL AND dead_lettered_at IS NULL
                                      AND lock_owner IS NULL THEN 1 ELSE 0 END),
                        SUM(CASE WHEN dispatched_at IS NULL AND dead_lettered_at IS NULL
                                      AND lock_owner IS NOT NULL THEN 1 ELSE 0 END),
                        SUM(CASE WHEN dispatched_at IS NOT NULL THEN 1 ELSE 0 END),
                        SUM(CASE WHEN dead_lettered_at IS NOT NULL THEN 1 ELSE 0 END),
                        (SELECT COUNT(*) FROM outbox_dispatch_failures)
                    FROM outbox_messages;
                    """;
                await using var reader = await query.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException("Outbox snapshot query returned no row.");
                }

                return new OutboxStoreSnapshot(
                    ReadCount(reader, 0),
                    ReadCount(reader, 1),
                    ReadCount(reader, 2),
                    ReadCount(reader, 3),
                    ReadCount(reader, 4));
            },
            cancellationToken);

    private static async Task<OutboxLease?> AcquireCoreAsync(
        SqliteConnection connection,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string? messageId;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT id
                FROM outbox_messages
                WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
                  AND COALESCE(available_at,occurred_at) <= $now
                  AND (lock_owner IS NULL OR lock_expires_at <= $now)
                ORDER BY COALESCE(available_at,occurred_at),occurred_at,id
                LIMIT 1;
                """;
            Add(select, "$now", Store(now));
            messageId = await select.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (messageId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var lockExpiresAt = now.Add(leaseDuration);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE outbox_messages
                SET lock_owner=$owner,lock_token=lock_token+1,lock_expires_at=$lockExpiresAt
                WHERE id=$messageId AND dispatched_at IS NULL AND dead_lettered_at IS NULL
                  AND (lock_owner IS NULL OR lock_expires_at <= $now);
                """;
            Add(update, "$owner", owner);
            Add(update, "$lockExpiresAt", Store(lockExpiresAt));
            Add(update, "$messageId", messageId);
            Add(update, "$now", Store(now));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Serialized outbox claim was lost.");
            }
        }

        var lease = await ReadLeaseAsync(
            connection,
            transaction,
            messageId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    private static async Task<OutboxMutationResult> MarkDispatchedCoreAsync(
        SqliteConnection connection,
        OutboxDispatchCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE outbox_messages
                SET dispatched_at=$dispatchedAt,lock_owner=NULL,lock_expires_at=NULL,last_error=NULL
                WHERE id=$messageId AND lock_owner=$owner AND lock_token=$token
                  AND lock_expires_at > $dispatchedAt
                  AND dispatched_at IS NULL AND dead_lettered_at IS NULL;
                """;
            Add(update, "$dispatchedAt", Store(command.DispatchedAt));
            Add(update, "$messageId", command.MessageId);
            Add(update, "$owner", command.Owner);
            Add(update, "$token", command.FencingToken);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
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

    private static async Task<OutboxMutationResult> RecordFailureCoreAsync(
        SqliteConnection connection,
        OutboxFailureCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText =
                """
                INSERT INTO outbox_dispatch_failures
                    (id,message_id,attempt,error,dead_lettered,occurred_at)
                VALUES ($failureId,$messageId,$attempt,$error,$deadLettered,$failedAt);

                UPDATE outbox_messages
                SET attempts=$attempt,last_error=$error,available_at=$availableAt,
                    dead_lettered_at=$deadLetteredAt,lock_owner=NULL,lock_expires_at=NULL
                WHERE id=$messageId AND lock_owner=$owner AND lock_token=$token
                  AND lock_expires_at > $failedAt
                  AND dispatched_at IS NULL AND dead_lettered_at IS NULL;
                """;
            Add(mutation, "$failureId", command.FailureId);
            Add(mutation, "$messageId", command.MessageId);
            Add(mutation, "$attempt", attempt);
            Add(mutation, "$error", command.Error);
            Add(mutation, "$deadLettered", deadLettered ? 1 : 0);
            Add(mutation, "$failedAt", Store(command.FailedAt));
            AddNullable(mutation, "$availableAt", availableAt is null ? null : Store(availableAt.Value));
            AddNullable(
                mutation,
                "$deadLetteredAt",
                deadLettered ? Store(command.FailedAt) : null);
            Add(mutation, "$owner", command.Owner);
            Add(mutation, "$token", command.FencingToken);
            if (await mutation.ExecuteNonQueryAsync(cancellationToken) != 2)
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

    private static async Task<OutboxLease> ReadLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT id,tenant_id,event_type,payload_json,occurred_at,attempts,
                   lock_owner,lock_token,lock_expires_at
            FROM outbox_messages WHERE id=$messageId;
            """;
        Add(query, "$messageId", messageId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Claimed outbox message disappeared.");
        }

        var payload = reader.GetString(3);
        OutboxContractValidator.ValidatePayload(payload, nameof(messageId));
        return new OutboxLease(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            payload,
            Parse(reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetInt64(7),
            Parse(reader.GetString(8)));
    }

    private static async Task<OutboxMutationRow?> ReadMutationRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT attempts,lock_owner,lock_token,lock_expires_at,dispatched_at,dead_lettered_at
            FROM outbox_messages WHERE id=$messageId;
            """;
        Add(query, "$messageId", messageId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OutboxMutationRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
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

    private static long ReadCount(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record OutboxMutationRow(
        int Attempts,
        string? LockOwner,
        long LockToken,
        DateTimeOffset? LockExpiresAt,
        bool Dispatched,
        bool DeadLettered);
}
