using System.Globalization;
using Harness.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Harness.RecoveryTests.Fixtures;

public sealed class SyntheticDurableTaskStore : IAsyncDisposable
{
    private readonly SqliteWriteDispatcher _dispatcher;

    private SyntheticDurableTaskStore(SqliteWriteDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static async Task<SyntheticDurableTaskStore> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, cancellationToken);
        var store = new SyntheticDurableTaskStore(dispatcher);

        try
        {
            await store.EnsureSchemaAsync(cancellationToken);
            return store;
        }
        catch
        {
            await dispatcher.DisposeAsync();
            throw;
        }
    }

    public Task EnqueueAsync(
        string taskId,
        int totalSteps,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSteps);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO synthetic_tasks (
                        id, state, total_steps, completed_steps, attempt_count,
                        reconciliation_count, owner, last_heartbeat_at, version
                    ) VALUES (
                        $id, 'pending', $totalSteps, 0, 0, 0, NULL, $now, 0
                    );
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$totalSteps", totalSteps);
                command.Parameters.AddWithValue("$now", ToStorage(now));
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    public async Task RunAsync(
        string taskId,
        string owner,
        int? pauseAfterStep,
        string? readySignalPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var acquired = await AcquireAsync(taskId, owner, DateTimeOffset.UtcNow, cancellationToken);
        if (!acquired)
        {
            throw new InvalidOperationException("Synthetic durable task could not be acquired from pending state.");
        }

        var snapshot = await ReadAsync(taskId, cancellationToken);
        for (var step = snapshot.CompletedSteps + 1; step <= snapshot.TotalSteps; step++)
        {
            await CommitStepAsync(taskId, owner, step, DateTimeOffset.UtcNow, cancellationToken);

            if (pauseAfterStep == step)
            {
                if (string.IsNullOrWhiteSpace(readySignalPath))
                {
                    throw new InvalidOperationException("A ready signal path is required when pausing a worker.");
                }

                await File.WriteAllTextAsync(readySignalPath, step.ToString(CultureInfo.InvariantCulture), cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        await CompleteAsync(taskId, owner, DateTimeOffset.UtcNow, cancellationToken);
    }

    public Task<int> ReconcileInterruptedAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE synthetic_tasks
                    SET state = 'pending',
                        owner = NULL,
                        reconciliation_count = reconciliation_count + 1,
                        last_heartbeat_at = $now,
                        version = version + 1
                    WHERE state = 'running'
                      AND last_heartbeat_at < $staleBefore;
                    """;
                command.Parameters.AddWithValue("$now", ToStorage(now));
                command.Parameters.AddWithValue("$staleBefore", ToStorage(staleBefore));
                return await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    public Task<SyntheticTaskSnapshot> ReadAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT t.id,
                           t.state,
                           t.total_steps,
                           t.completed_steps,
                           (SELECT COUNT(*) FROM synthetic_checkpoints c WHERE c.task_id = t.id),
                           t.attempt_count,
                           t.reconciliation_count,
                           t.owner,
                           t.last_heartbeat_at
                    FROM synthetic_tasks t
                    WHERE t.id = $id;
                    """;
                command.Parameters.AddWithValue("$id", taskId);

                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException("Synthetic durable task was not found.");
                }

                return new SyntheticTaskSnapshot(
                    reader.GetString(0),
                    ParseState(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            },
            cancellationToken);
    }

    public ValueTask DisposeAsync() => _dispatcher.DisposeAsync();

    private Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS synthetic_tasks (
                        id TEXT PRIMARY KEY,
                        state TEXT NOT NULL CHECK (state IN ('pending', 'running', 'completed')),
                        total_steps INTEGER NOT NULL CHECK (total_steps > 0),
                        completed_steps INTEGER NOT NULL DEFAULT 0,
                        attempt_count INTEGER NOT NULL DEFAULT 0,
                        reconciliation_count INTEGER NOT NULL DEFAULT 0,
                        owner TEXT NULL,
                        last_heartbeat_at TEXT NOT NULL,
                        version INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE TABLE IF NOT EXISTS synthetic_checkpoints (
                        task_id TEXT NOT NULL,
                        step INTEGER NOT NULL,
                        committed_at TEXT NOT NULL,
                        PRIMARY KEY (task_id, step),
                        FOREIGN KEY (task_id) REFERENCES synthetic_tasks(id)
                    );
                    """;
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    private Task<bool> AcquireAsync(
        string taskId,
        string owner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE synthetic_tasks
                    SET state = 'running',
                        owner = $owner,
                        attempt_count = attempt_count + 1,
                        last_heartbeat_at = $now,
                        version = version + 1
                    WHERE id = $id
                      AND state = 'pending';
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$owner", owner);
                command.Parameters.AddWithValue("$now", ToStorage(now));
                return await command.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);
    }

    private Task CommitStepAsync(
        string taskId,
        string owner,
        int step,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);

                await using var checkpoint = connection.CreateCommand();
                checkpoint.Transaction = transaction;
                checkpoint.CommandText =
                    """
                    INSERT OR IGNORE INTO synthetic_checkpoints (task_id, step, committed_at)
                    VALUES ($taskId, $step, $now);
                    """;
                checkpoint.Parameters.AddWithValue("$taskId", taskId);
                checkpoint.Parameters.AddWithValue("$step", step);
                checkpoint.Parameters.AddWithValue("$now", ToStorage(now));
                await checkpoint.ExecuteNonQueryAsync(token);

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE synthetic_tasks
                    SET completed_steps = (
                            SELECT COUNT(*)
                            FROM synthetic_checkpoints
                            WHERE task_id = $taskId
                        ),
                        last_heartbeat_at = $now,
                        version = version + 1
                    WHERE id = $taskId
                      AND state = 'running'
                      AND owner = $owner;
                    """;
                update.Parameters.AddWithValue("$taskId", taskId);
                update.Parameters.AddWithValue("$owner", owner);
                update.Parameters.AddWithValue("$now", ToStorage(now));

                if (await update.ExecuteNonQueryAsync(token) != 1)
                {
                    throw new InvalidOperationException("Synthetic durable task ownership was lost while committing.");
                }

                await transaction.CommitAsync(token);
            },
            cancellationToken);
    }

    private Task CompleteAsync(
        string taskId,
        string owner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE synthetic_tasks
                    SET state = 'completed',
                        owner = NULL,
                        last_heartbeat_at = $now,
                        version = version + 1
                    WHERE id = $id
                      AND state = 'running'
                      AND owner = $owner
                      AND completed_steps = total_steps;
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$owner", owner);
                command.Parameters.AddWithValue("$now", ToStorage(now));

                if (await command.ExecuteNonQueryAsync(token) != 1)
                {
                    throw new InvalidOperationException("Synthetic durable task could not be completed consistently.");
                }
            },
            cancellationToken);
    }

    private static string ToStorage(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static SyntheticTaskState ParseState(string state) => state switch
    {
        "pending" => SyntheticTaskState.Pending,
        "running" => SyntheticTaskState.Running,
        "completed" => SyntheticTaskState.Completed,
        _ => throw new InvalidOperationException($"Unknown synthetic task state '{state}'."),
    };
}
