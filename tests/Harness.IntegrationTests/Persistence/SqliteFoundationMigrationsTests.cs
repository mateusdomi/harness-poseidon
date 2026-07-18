using System.Globalization;
using Harness.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Harness.IntegrationTests.Persistence;

public sealed class SqliteFoundationMigrationsTests
{
    [Fact]
    public async Task FoundationTransactionIsAtomicIdempotentAndAudited()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-foundation-transaction-sqlite",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "foundation.db"),
                timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await FoundationTransactionBehavior.AssertAsync(
                new SqliteFoundationTransactionStore(dispatcher),
                timeout.Token);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FoundationMigrationIsIdempotentAndEnforcesTenantRelationships()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-foundation-sqlite",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var databasePath = Path.Combine(artifactRoot, "foundation.db");
        Directory.CreateDirectory(artifactRoot);

        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token);
            Assert.Equal(1, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
            Assert.Equal(0, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));

            var tableCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*)
                        FROM sqlite_master
                        WHERE type = 'table'
                          AND name IN ('tenants', 'organizations', 'projects', 'local_users',
                                       'inbox_messages', 'outbox_messages', 'audit_ledger');
                        """;
                    return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(7, tableCount);

            await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO tenants (id, name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Tenant', '2026-07-18T13:30:00.0000000+00:00');
                        INSERT INTO organizations (id, tenant_id, name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAW', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Organization', '2026-07-18T13:30:00.0000000+00:00');
                        INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FAV', '01ARZ3NDEKTSV4RRFFQ69G5FAW', 'Project', '2026-07-18T13:30:00.0000000+00:00');
                        INSERT INTO local_users (id, tenant_id, display_name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAY', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Local User', '2026-07-18T13:30:00.0000000+00:00');
                        """;
                    await command.ExecuteNonQueryAsync(token);
                },
                timeout.Token);

            var exception = await Assert.ThrowsAsync<SqliteException>(() =>
                dispatcher.ExecuteAsync(
                    async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText =
                            """
                            INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAZ', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                                    '01ARZ3NDEKTSV4RRFFQ69G5FB0', 'Invalid', '2026-07-18T13:30:00.0000000+00:00');
                            """;
                        await command.ExecuteNonQueryAsync(token);
                    },
                    timeout.Token));
            Assert.Equal(19, exception.SqliteErrorCode);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }
}
