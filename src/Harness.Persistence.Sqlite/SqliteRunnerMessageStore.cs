using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.RunnerIpc;
using Harness.SharedKernel.RunnerIpc;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteRunnerMessageStore : IRunnerMessageStore, IAsyncDisposable
{
    private readonly Lazy<Task<SqliteWriteDispatcher>> _dispatcher;
    private int _disposed;

    public SqliteRunnerMessageStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _dispatcher = new Lazy<Task<SqliteWriteDispatcher>>(
            () => OpenAsync(databasePath),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<RunnerMessageStoreResult> ApplyAsync(
        RunnerMessageEnvelope message,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var dispatcher = await _dispatcher.Value.WaitAsync(cancellationToken);
        return await dispatcher.ExecuteAsync(
            (connection, token) => ApplyCoreAsync(connection, message, occurredAt, token),
            cancellationToken);
    }

    public async Task<RunnerAttemptState?> ReadAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var dispatcher = await _dispatcher.Value.WaitAsync(cancellationToken);
        return await dispatcher.ExecuteAsync(
            (connection, token) => ReadAttemptCoreAsync(connection, attemptId, null, token),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_dispatcher.IsValueCreated)
        {
            return;
        }

        await using var dispatcher = await _dispatcher.Value;
    }

    private static async Task<SqliteWriteDispatcher> OpenAsync(string databasePath)
    {
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath);
        try
        {
            await SqliteMigrationRunner.ApplyAsync(dispatcher);
            return dispatcher;
        }
        catch
        {
            await dispatcher.DisposeAsync();
            throw;
        }
    }

    private static async Task<RunnerMessageStoreResult> ApplyCoreAsync(
        SqliteConnection connection,
        RunnerMessageEnvelope message,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fingerprint = RunnerMessageFingerprint.Compute(message);
        var replay = await ReadInboxAsync(connection, transaction, message, fingerprint, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var current = await ReadAttemptCoreAsync(connection, message.AttemptId, transaction, cancellationToken);
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
        var timestamp = occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        if (current is null)
        {
            await InsertAttemptAsync(
                connection,
                transaction,
                message,
                heartbeatCount,
                completed,
                inboxCount,
                timestamp,
                cancellationToken);
        }
        else
        {
            await UpdateAttemptAsync(
                connection,
                transaction,
                message,
                heartbeatCount,
                completed,
                inboxCount,
                version,
                timestamp,
                cancellationToken);
        }

        if (message.Type == RunnerMessageTypes.Checkpoint)
        {
            await InsertCheckpointAsync(connection, transaction, message, timestamp, cancellationToken);
        }

        var receipt = new RunnerMessageReceipt(message.AttemptId, message.Sequence, Applied: true, Replay: false);
        await InsertMessagingAsync(
            connection,
            transaction,
            message,
            fingerprint,
            receipt,
            timestamp,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RunnerMessageStoreResult.Succeeded(receipt);
    }

    private static async Task<RunnerMessageStoreResult?> ReadInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunnerMessageEnvelope message,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT message_hash, response_json
            FROM runner_inbox_messages
            WHERE attempt_id = $attemptId AND idempotency_key = $idempotencyKey;
            """;
        Add(command, "$attemptId", message.AttemptId);
        Add(command, "$idempotencyKey", message.IdempotencyKey);
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

    private static async Task InsertAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunnerMessageEnvelope message,
        int heartbeatCount,
        bool completed,
        int inboxCount,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runner_attempts
                (attempt_id, runner_id, last_sequence, heartbeat_count, completed, inbox_count, version, updated_at)
            VALUES
                ($attemptId, $runnerId, $sequence, $heartbeatCount, $completed, $inboxCount, 1, $updatedAt);
            """;
        Add(command, "$attemptId", message.AttemptId);
        Add(command, "$runnerId", message.RunnerId);
        Add(command, "$sequence", message.Sequence);
        Add(command, "$heartbeatCount", heartbeatCount);
        Add(command, "$completed", completed ? 1 : 0);
        Add(command, "$inboxCount", inboxCount);
        Add(command, "$updatedAt", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunnerMessageEnvelope message,
        int heartbeatCount,
        bool completed,
        int inboxCount,
        long version,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE runner_attempts
            SET last_sequence = $sequence,
                heartbeat_count = $heartbeatCount,
                completed = $completed,
                inbox_count = $inboxCount,
                version = $version,
                updated_at = $updatedAt
            WHERE attempt_id = $attemptId;
            """;
        Add(command, "$attemptId", message.AttemptId);
        Add(command, "$sequence", message.Sequence);
        Add(command, "$heartbeatCount", heartbeatCount);
        Add(command, "$completed", completed ? 1 : 0);
        Add(command, "$inboxCount", inboxCount);
        Add(command, "$version", version);
        Add(command, "$updatedAt", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunnerMessageEnvelope message,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runner_checkpoints (attempt_id, sequence, checkpoint_id, created_at)
            VALUES ($attemptId, $sequence, $checkpointId, $createdAt);
            """;
        Add(command, "$attemptId", message.AttemptId);
        Add(command, "$sequence", message.Sequence);
        Add(command, "$checkpointId", message.Payload.GetProperty("checkpointId").GetString()!);
        Add(command, "$createdAt", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMessagingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunnerMessageEnvelope message,
        string fingerprint,
        RunnerMessageReceipt receipt,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runner_outbox_messages
                (attempt_id, sequence, event_type, payload_json, occurred_at)
            VALUES
                ($attemptId, $sequence, $eventType, $payloadJson, $occurredAt);
            INSERT INTO runner_inbox_messages
                (attempt_id, idempotency_key, message_hash, response_json, processed_at)
            VALUES
                ($attemptId, $idempotencyKey, $messageHash, $responseJson, $processedAt);
            """;
        Add(command, "$attemptId", message.AttemptId);
        Add(command, "$sequence", message.Sequence);
        Add(command, "$eventType", RunnerOutboxEvent.TypeFor(message.Type));
        Add(command, "$payloadJson", JsonSerializer.Serialize(message));
        Add(command, "$occurredAt", timestamp);
        Add(command, "$idempotencyKey", message.IdempotencyKey);
        Add(command, "$messageHash", fingerprint);
        Add(command, "$responseJson", JsonSerializer.Serialize(receipt));
        Add(command, "$processedAt", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RunnerAttemptState?> ReadAttemptCoreAsync(
        SqliteConnection connection,
        string attemptId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        string runnerId;
        long lastSequence;
        int heartbeatCount;
        bool completed;
        int inboxCount;
        long version;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT runner_id, last_sequence, heartbeat_count, completed, inbox_count, version
                FROM runner_attempts
                WHERE attempt_id = $attemptId;
                """;
            Add(command, "$attemptId", attemptId);
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
        }

        var checkpoints = new List<string>();
        await using (var checkpointCommand = connection.CreateCommand())
        {
            checkpointCommand.Transaction = transaction;
            checkpointCommand.CommandText =
                """
                SELECT checkpoint_id
                FROM runner_checkpoints
                WHERE attempt_id = $attemptId
                ORDER BY sequence;
                """;
            Add(checkpointCommand, "$attemptId", attemptId);
            await using var reader = await checkpointCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                checkpoints.Add(reader.GetString(0));
            }
        }

        await using var outboxCommand = connection.CreateCommand();
        outboxCommand.Transaction = transaction;
        outboxCommand.CommandText =
            "SELECT COUNT(*) FROM runner_outbox_messages WHERE attempt_id = $attemptId;";
        Add(outboxCommand, "$attemptId", attemptId);
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
            version);
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
