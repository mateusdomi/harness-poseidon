using System.Reflection;
using Npgsql;

namespace Harness.Persistence.Postgres;

public sealed class PostgresMigrationRunner
{
    private const long MigrationLockId = 3_265_001_008_001;

    public static async Task<int> ApplyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock($1);",
            cancellationToken,
            new NpgsqlParameter<long> { TypedValue = MigrationLockId });
        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE SCHEMA IF NOT EXISTS harness;
            CREATE TABLE IF NOT EXISTS harness.schema_migrations
            (
                migration_name text PRIMARY KEY,
                applied_at timestamptz NOT NULL
            );
            """,
            cancellationToken);

        var appliedCount = 0;
        foreach (var migration in ReadMigrations())
        {
            if (await IsAppliedAsync(connection, transaction, migration.Name, cancellationToken))
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.schema_migrations (migration_name, applied_at)
                VALUES ($1, $2);
                """,
                cancellationToken,
                new NpgsqlParameter<string> { TypedValue = migration.Name },
                new NpgsqlParameter<DateTimeOffset> { TypedValue = DateTimeOffset.UtcNow });
            appliedCount++;
        }

        await transaction.CommitAsync(cancellationToken);
        return appliedCount;
    }

    private static async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string migrationName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM harness.schema_migrations WHERE migration_name = $1);";
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = migrationName });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return migration state."));
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

    private static PostgresMigration[] ReadMigrations()
    {
        var assembly = typeof(PostgresMigrationRunner).Assembly;
        return assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) &&
                name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => new PostgresMigration(MigrationName(name), ReadResource(assembly, name)))
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

    private sealed record PostgresMigration(string Name, string Sql);
}
