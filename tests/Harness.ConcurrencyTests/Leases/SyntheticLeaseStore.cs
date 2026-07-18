using System.Globalization;
using Harness.Persistence.Sqlite;

namespace Harness.ConcurrencyTests.Leases;

public sealed class SyntheticLeaseStore : IAsyncDisposable
{
    private readonly SqliteWriteDispatcher _dispatcher;

    private SyntheticLeaseStore(SqliteWriteDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static async Task<SyntheticLeaseStore> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, cancellationToken);
        var store = new SyntheticLeaseStore(dispatcher);

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

    public Task EnsureResourceAsync(
        string resourceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO synthetic_leases (
                        resource_id, owner, fencing_token, expires_at, value, version
                    ) VALUES (
                        $resourceId, NULL, 0, $now, NULL, 0
                    );
                    """;
                command.Parameters.AddWithValue("$resourceId", resourceId);
                command.Parameters.AddWithValue("$now", ToStorage(now));
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    public Task<long?> AcquireAsync(
        string resourceId,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        return _dispatcher.ExecuteAsync<long?>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE synthetic_leases
                    SET owner = $owner,
                        fencing_token = fencing_token + 1,
                        expires_at = $expiresAt,
                        version = version + 1
                    WHERE resource_id = $resourceId
                      AND (owner IS NULL OR expires_at <= $now)
                    RETURNING fencing_token;
                    """;
                command.Parameters.AddWithValue("$resourceId", resourceId);
                command.Parameters.AddWithValue("$owner", owner);
                command.Parameters.AddWithValue("$now", ToStorage(now));
                command.Parameters.AddWithValue("$expiresAt", ToStorage(now.Add(leaseDuration)));
                var result = await command.ExecuteScalarAsync(token);
                return result is null ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
            },
            cancellationToken);
    }

    public Task<bool> RenewAsync(
        string resourceId,
        string owner,
        long fencingToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE synthetic_leases
                    SET expires_at = $expiresAt,
                        version = version + 1
                    WHERE resource_id = $resourceId
                      AND owner = $owner
                      AND fencing_token = $fencingToken
                      AND expires_at > $now;
                    """;
                command.Parameters.AddWithValue("$resourceId", resourceId);
                command.Parameters.AddWithValue("$owner", owner);
                command.Parameters.AddWithValue("$fencingToken", fencingToken);
                command.Parameters.AddWithValue("$now", ToStorage(now));
                command.Parameters.AddWithValue("$expiresAt", ToStorage(now.Add(leaseDuration)));
                return await command.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);
    }

    public Task<bool> WriteAsync(
        string resourceId,
        string owner,
        long fencingToken,
        string value,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE synthetic_leases
                    SET value = $value,
                        version = version + 1
                    WHERE resource_id = $resourceId
                      AND owner = $owner
                      AND fencing_token = $fencingToken
                      AND expires_at > $now;
                    """;
                command.Parameters.AddWithValue("$resourceId", resourceId);
                command.Parameters.AddWithValue("$owner", owner);
                command.Parameters.AddWithValue("$fencingToken", fencingToken);
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue("$now", ToStorage(now));
                return await command.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);
    }

    public Task<SyntheticLeaseSnapshot> ReadAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT resource_id, owner, fencing_token, expires_at, value, version
                    FROM synthetic_leases
                    WHERE resource_id = $resourceId;
                    """;
                command.Parameters.AddWithValue("$resourceId", resourceId);

                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException("Synthetic lease resource was not found.");
                }

                return new SyntheticLeaseSnapshot(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5));
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
                    CREATE TABLE IF NOT EXISTS synthetic_leases (
                        resource_id TEXT PRIMARY KEY,
                        owner TEXT NULL,
                        fencing_token INTEGER NOT NULL,
                        expires_at TEXT NOT NULL,
                        value TEXT NULL,
                        version INTEGER NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    private static string ToStorage(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
