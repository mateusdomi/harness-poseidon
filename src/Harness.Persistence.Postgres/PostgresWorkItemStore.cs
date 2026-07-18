using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed class PostgresWorkItemStore(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<int> ApplyMigrationsAsync(CancellationToken cancellationToken = default) =>
        PostgresMigrationRunner.ApplyAsync(_dataSource, cancellationToken);

    public async Task<bool> EnqueueAsync(
        string workItemId,
        string payload,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO harness_poc.work_items (id, payload, state, created_at)
            VALUES ($1, $2, 'ready', $3)
            ON CONFLICT (id) DO NOTHING;
            """);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = workItemId });
        command.Parameters.Add(new NpgsqlParameter<string>
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            TypedValue = payload,
        });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = createdAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<PostgresWorkItemLease?> TryAcquireNextAsync(
        string ownerId,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH candidate AS
            (
                SELECT id
                FROM harness_poc.work_items
                WHERE state = 'ready'
                   OR (state = 'leased' AND lease_expires_at <= $2)
                ORDER BY created_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE harness_poc.work_items AS work_item
            SET state = 'leased',
                owner_id = $1,
                lease_token = work_item.lease_token + 1,
                lease_expires_at = $3,
                version = work_item.version + 1
            FROM candidate
            WHERE work_item.id = candidate.id
            RETURNING work_item.id,
                      work_item.owner_id,
                      work_item.lease_token,
                      work_item.lease_expires_at,
                      work_item.version;
            """;
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ownerId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now.Add(leaseDuration) });

        PostgresWorkItemLease? lease = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                lease = new PostgresWorkItemLease(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetInt64(4));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    public async Task<bool> CompleteAsync(
        string workItemId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fencingToken, 0);

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE harness_poc.work_items
            SET state = 'completed',
                lease_expires_at = NULL,
                version = version + 1
            WHERE id = $1
              AND state = 'leased'
              AND lease_token = $2;
            """);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = workItemId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = fencingToken });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<long> CountAsync(string state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        await using var command = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM harness_poc.work_items WHERE state = $1;");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = state });
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return a work item count."));
    }
}
