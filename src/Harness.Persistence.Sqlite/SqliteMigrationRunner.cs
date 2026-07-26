using System.Reflection;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public static class SqliteMigrationRunner
{
    public static Task<int> ApplyAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return dispatcher.ExecuteAsync(
            (connection, token) => ApplyCoreAsync(connection, token),
            cancellationToken);
    }

    private static async Task<int> ApplyCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var foreignKeysEnabled = await ReadForeignKeysEnabledAsync(
            connection,
            cancellationToken);
        if (foreignKeysEnabled)
        {
            await ExecuteConnectionAsync(
                connection,
                "PRAGMA foreign_keys=OFF;",
                cancellationToken);
        }

        try
        {
            await using var transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(
                connection,
                (SqliteTransaction)transaction,
                """
                CREATE TABLE IF NOT EXISTS schema_migrations
                (
                    migration_name TEXT PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                """,
                cancellationToken);

            var appliedCount = 0;
            foreach (var migration in ReadMigrations())
            {
                if (await IsAppliedAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    migration.Name,
                    cancellationToken))
                {
                    continue;
                }

                await ExecuteAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    migration.Sql,
                    cancellationToken);
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText =
                    "INSERT INTO schema_migrations (migration_name, applied_at) VALUES ($name, $appliedAt);";
                insert.Parameters.AddWithValue("$name", migration.Name);
                insert.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                appliedCount++;
            }

            await ValidateForeignKeysAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return appliedCount;
        }
        finally
        {
            if (foreignKeysEnabled)
            {
                await ExecuteConnectionAsync(
                    connection,
                    "PRAGMA foreign_keys=ON;",
                    CancellationToken.None);
            }
        }
    }

    private static async Task<bool> ReadForeignKeysEnabledAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ValidateForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            var rowId = reader.IsDBNull(1)
                ? "unknown"
                : reader.GetInt64(1).ToString(CultureInfo.InvariantCulture);
            var parent = reader.GetString(2);
            throw new InvalidOperationException(
                $"Migration produced an invalid foreign key from {table} row {rowId} to {parent}.");
        }
    }

    private static async Task ExecuteConnectionAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsAppliedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string migrationName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE migration_name = $name);";
        command.Parameters.AddWithValue("$name", migrationName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteMigration[] ReadMigrations()
    {
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        return assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) &&
                name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => new SqliteMigration(MigrationName(name), ReadResource(assembly, name)))
            .ToArray();
    }

    private static string MigrationName(string resourceName)
    {
        var marker = resourceName.IndexOf(".Migrations.", StringComparison.Ordinal);
        return resourceName[(marker + ".Migrations.".Length)..^4];
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration was not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record SqliteMigration(string Name, string Sql);
}
