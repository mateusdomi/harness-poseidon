using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// Upgrade de release: um banco parado em qualquer prefixo histórico de
/// migrations (instalação antiga) precisa subir até a cabeça com o runner
/// idempotente e ficar utilizável — inclusive pelas colunas novas (role,
/// external_subject) que ganham defaults para dados pré-existentes.
/// </summary>
public sealed class SqliteMigrationUpgradeTests
{
    private const int HeadCount = 66;
    private const string UpgradeSolicitationId = "01ARZ3NDEKTSV4RRFFQ69G5F80";

    [Theory]
    [InlineData(10)]
    [InlineData(22)]
    [InlineData(33)]
    [InlineData(65)]
    public async Task UpgradesFromAnyHistoricalSchemaPrefix(int prefixCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"upgrade-{prefixCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "harness.db");
        var migrations = ReadEmbeddedMigrations();
        Assert.Equal(HeadCount, migrations.Length);

        try
        {
            // Instalação "antiga": somente o prefixo histórico aplicado, com a
            // mesma contabilidade de schema_migrations do runner real.
            await using (var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                databasePath, timeout.Token))
            {
                await dispatcher.ExecuteAsync(
                    async (connection, token) =>
                    {
                        await using var transaction =
                            await connection.BeginTransactionAsync(token);
                        await using (var bookkeeping = connection.CreateCommand())
                        {
                            bookkeeping.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                            bookkeeping.CommandText =
                                """
                                CREATE TABLE IF NOT EXISTS schema_migrations
                                (
                                    migration_name TEXT PRIMARY KEY,
                                    applied_at TEXT NOT NULL
                                );
                                """;
                            await bookkeeping.ExecuteNonQueryAsync(token);
                        }

                        foreach (var (name, sql) in migrations.Take(prefixCount))
                        {
                            await using (var apply = connection.CreateCommand())
                            {
                                apply.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                                apply.CommandText = sql;
                                await apply.ExecuteNonQueryAsync(token);
                            }

                            await using var record = connection.CreateCommand();
                            record.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                            record.CommandText =
                                "INSERT INTO schema_migrations (migration_name, applied_at) VALUES ($name, $appliedAt);";
                            record.Parameters.AddWithValue("$name", name);
                            record.Parameters.AddWithValue(
                                "$appliedAt",
                                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                            await record.ExecuteNonQueryAsync(token);
                        }

                        await transaction.CommitAsync(token);
                        return prefixCount;
                    },
                    timeout.Token);

                if (prefixCount == HeadCount - 1)
                {
                    await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                        FoundationTransactionBehavior.Command(),
                        timeout.Token);
                    const string instruction = "Preserve this task across the state migration.";
                    await new SqliteWorkChainStore(dispatcher).CreateAsync(
                        new WorkChainCreateCommand(
                            FoundationTransactionBehavior.TenantId,
                            FoundationTransactionBehavior.ProjectId,
                            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                            UpgradeSolicitationId,
                            "Upgrade the populated work-chain schema.",
                            "01ARZ3NDEKTSV4RRFFQ69G5F81",
                            "Preserve the demand",
                            "[\"State and relationships survive\"]",
                            "01ARZ3NDEKTSV4RRFFQ69G5F82",
                            "Preserve the task",
                            "medium",
                            3m,
                            "01ARZ3NDEKTSV4RRFFQ69G5F83",
                            instruction,
                            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instruction))),
                            "upgrade:work-chain:create",
                            DateTimeOffset.UtcNow),
                        timeout.Token);
                }
            }

            // Upgrade real: reabre o banco e o runner aplica somente o restante.
            await using (var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                databasePath, timeout.Token))
            {
                Assert.Equal(
                    HeadCount - prefixCount,
                    await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
                Assert.Equal(0, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
                Assert.True(
                    (await dispatcher.ReadPragmaStateAsync(timeout.Token)).ForeignKeysEnabled);

                if (prefixCount == HeadCount - 1)
                {
                    var preserved = await new SqliteWorkChainStore(dispatcher).ReadAggregateAsync(
                        FoundationTransactionBehavior.TenantId,
                        UpgradeSolicitationId,
                        timeout.Token);
                    Assert.NotNull(preserved);
                    var task = Assert.Single(Assert.Single(preserved.Demands).Tasks);
                    Assert.Equal("ready", task.State);
                    Assert.Single(task.Instructions);
                }
                else
                {
                    var store = new SqliteLocalProfileStore(dispatcher);
                    var now = DateTimeOffset.UtcNow;
                    var created = await store.CreateAsync(
                        new LocalProfileCreateCommand(
                            UlidValue.New(now).ToString(), "Personal",
                            UlidValue.New(now).ToString(), "Upgrade", null, null, "pt-BR", now),
                        timeout.Token);
                    Assert.Equal(LocalProfileMutationStatus.Applied, created.Status);
                    Assert.Equal(LocalProfileRole.Admin, created.Profile!.Role);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static (string Name, string Sql)[] ReadEmbeddedMigrations()
    {
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        return assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) &&
                name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var marker = name.IndexOf(".Migrations.", StringComparison.Ordinal);
                return (name[(marker + ".Migrations.".Length)..^4], reader.ReadToEnd());
            })
            .ToArray();
    }
}
