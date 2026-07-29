using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.RunnerIpc;
using Harness.SharedKernel.RunnerIpc;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresRunnerMessageStore(NpgsqlDataSource dataSource) : IRunnerMessageStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<RunnerMessageStoreResult> ApplyAsync(
        RunnerMessageEnvelope message,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text(message.AttemptId));

        var fingerprint = RunnerMessageFingerprint.Compute(message);
        var replay = await ReadInboxAsync(connection, transaction, message, fingerprint, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var current = await ReadAttemptCoreAsync(connection, transaction, message.AttemptId, cancellationToken);
        var rejection = RunnerMessageTransition.RejectInvalid(current, message);
        if (rejection is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return rejection;
        }

        var heartbeatCount = (current?.HeartbeatCount ?? 0) +
            (message.Type == RunnerMessageTypes.Heartbeat ? 1 : 0);
        var completed = (current?.Completed ?? false) || message.Type == RunnerMessageTypes.Completion;
        var inboxCount = (current?.InboxCount ?? 0) + 1;
        var version = (current?.Version ?? 0) + 1;

        if (current is null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.runner_attempts
                    (attempt_id, runner_id, last_sequence, heartbeat_count, completed, inbox_count, version, updated_at, fencing_token)
                VALUES ($1, $2, $3, $4, $5, $6, 1, $7, $8);
                """,
                cancellationToken,
                Text(message.AttemptId),
                Text(message.RunnerId),
                Bigint(message.Sequence),
                Integer(heartbeatCount),
                Boolean(completed),
                Integer(inboxCount),
                Timestamp(occurredAt),
                Bigint(message.FencingToken));
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE harness.runner_attempts
                SET last_sequence = $1,
                    heartbeat_count = $2,
                    completed = $3,
                    inbox_count = $4,
                    version = $5,
                    updated_at = $6,
                    runner_id = $8,
                    fencing_token = GREATEST(fencing_token, $9)
                WHERE attempt_id = $7;
                """,
                cancellationToken,
                Bigint(message.Sequence),
                Integer(heartbeatCount),
                Boolean(completed),
                Integer(inboxCount),
                Bigint(version),
                Timestamp(occurredAt),
                Text(message.AttemptId),
                Text(message.RunnerId),
                Bigint(message.FencingToken));
        }

        if (message.Type == RunnerMessageTypes.Checkpoint)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.runner_checkpoints (attempt_id, sequence, checkpoint_id, created_at)
                VALUES ($1, $2, $3, $4);
                """,
                cancellationToken,
                Text(message.AttemptId),
                Bigint(message.Sequence),
                Text(message.Payload.GetProperty("checkpointId").GetString()!),
                Timestamp(occurredAt));
        }

        var receipt = new RunnerMessageReceipt(message.AttemptId, message.Sequence, Applied: true, Replay: false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.runner_outbox_messages
                (attempt_id, sequence, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            Text(message.AttemptId),
            Bigint(message.Sequence),
            Text(RunnerOutboxEvent.TypeFor(message.Type)),
            Json(JsonSerializer.Serialize(message)),
            Timestamp(occurredAt));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.runner_inbox_messages
                (attempt_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES ($1, $2, $3, $4, $5);
            """,
            cancellationToken,
            Text(message.AttemptId),
            Text(message.IdempotencyKey),
            Text(fingerprint),
            Json(JsonSerializer.Serialize(receipt)),
            Timestamp(occurredAt));

        await transaction.CommitAsync(cancellationToken);
        return RunnerMessageStoreResult.Succeeded(receipt);
    }

    public async Task<RunnerAttemptState?> ReadAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAttemptCoreAsync(connection, null, attemptId, cancellationToken);
    }

    private static async Task<RunnerMessageStoreResult?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RunnerMessageEnvelope message,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT message_hash, response_json::text
            FROM harness.runner_inbox_messages
            WHERE attempt_id = $1 AND idempotency_key = $2;
            """;
        command.Parameters.Add(Text(message.AttemptId));
        command.Parameters.Add(Text(message.IdempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), fingerprint, StringComparison.Ordinal))
        {
            return RunnerMessageStoreResult.Rejected(RunnerMessageRejection.IdempotencyKeyConflict);
        }

        var receipt = JsonSerializer.Deserialize<RunnerMessageReceipt>(reader.GetString(1))
            ?? throw new InvalidOperationException("The persisted Runner receipt is invalid.");
        return RunnerMessageStoreResult.Succeeded(receipt with { Applied = false, Replay = true });
    }

    private static async Task<RunnerAttemptState?> ReadAttemptCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        string runnerId;
        long lastSequence;
        long fencingToken;
        int heartbeatCount;
        bool completed;
        int inboxCount;
        long version;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT runner_id, last_sequence, heartbeat_count, completed, inbox_count, version, fencing_token
                FROM harness.runner_attempts
                WHERE attempt_id = $1;
                """;
            command.Parameters.Add(Text(attemptId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            runnerId = reader.GetString(0);
            lastSequence = reader.GetInt64(1);
            heartbeatCount = reader.GetInt32(2);
            completed = reader.GetBoolean(3);
            inboxCount = reader.GetInt32(4);
            version = reader.GetInt64(5);
            fencingToken = reader.GetInt64(6);
        }

        var checkpoints = new List<string>();
        await using (var checkpointCommand = connection.CreateCommand())
        {
            checkpointCommand.Transaction = transaction;
            checkpointCommand.CommandText =
                """
                SELECT checkpoint_id
                FROM harness.runner_checkpoints
                WHERE attempt_id = $1
                ORDER BY sequence;
                """;
            checkpointCommand.Parameters.Add(Text(attemptId));
            await using var reader = await checkpointCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                checkpoints.Add(reader.GetString(0));
            }
        }

        await using var outboxCommand = connection.CreateCommand();
        outboxCommand.Transaction = transaction;
        outboxCommand.CommandText =
            "SELECT COUNT(*) FROM harness.runner_outbox_messages WHERE attempt_id = $1;";
        outboxCommand.Parameters.Add(Text(attemptId));
        var outboxCount = Convert.ToInt32(
            await outboxCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        return new RunnerAttemptState(
            runnerId,
            attemptId,
            lastSequence,
            heartbeatCount,
            checkpoints,
            completed,
            inboxCount,
            outboxCount,
            version,
            fencingToken);
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

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

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
