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
    private const int HeadCount = 73;
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
                    await SeedLegacyReadyTaskAsync(
                        dispatcher,
                        instruction,
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

    private static Task<int> SeedLegacyReadyTaskAsync(
        SqliteWriteDispatcher dispatcher,
        string instruction,
        CancellationToken cancellationToken) =>
        dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                var occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                await using var transaction = await connection.BeginTransactionAsync(token);
                await using var command = connection.CreateCommand();
                command.Transaction =
                    (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                command.CommandText =
                    """
                    INSERT INTO solicitations
                        (id,tenant_id,project_id,user_id,content,created_at,kind,title,state,is_internal)
                    VALUES
                        ($solicitationId,$tenantId,$projectId,$userId,$content,$occurredAt,
                         'request','Upgrade task','open',0);
                    INSERT INTO demands
                        (id,tenant_id,project_id,solicitation_id,title,acceptance_criteria_json,
                         created_at,description,state,priority,source_solicitation_id,is_internal)
                    VALUES
                        ($demandId,$tenantId,$projectId,$solicitationId,'Preserve the demand',
                         '["State and relationships survive"]',$occurredAt,$content,'open',
                         'medium',$solicitationId,0);
                    INSERT INTO work_tasks
                        (id,tenant_id,project_id,demand_id,title,risk_tier,weight,state,version,
                         created_at,updated_at,source_demand_id,board_state,priority)
                    VALUES
                        ($taskId,$tenantId,$projectId,$demandId,'Preserve the task','medium',3,
                         'ready',1,$occurredAt,$occurredAt,$demandId,'ready','medium');
                    INSERT INTO instruction_versions
                        (id,tenant_id,project_id,task_id,version,content,content_hash,created_at,
                         author_kind)
                    VALUES
                        ($instructionId,$tenantId,$projectId,$taskId,1,$instruction,$instructionHash,
                         $occurredAt,'chief');
                    """;
                command.Parameters.AddWithValue(
                    "$tenantId",
                    FoundationTransactionBehavior.TenantId);
                command.Parameters.AddWithValue(
                    "$projectId",
                    FoundationTransactionBehavior.ProjectId);
                command.Parameters.AddWithValue(
                    "$userId",
                    "01ARZ3NDEKTSV4RRFFQ69G5FAY");
                command.Parameters.AddWithValue("$solicitationId", UpgradeSolicitationId);
                command.Parameters.AddWithValue(
                    "$content",
                    "Upgrade the populated work-chain schema.");
                command.Parameters.AddWithValue(
                    "$demandId",
                    "01ARZ3NDEKTSV4RRFFQ69G5F81");
                command.Parameters.AddWithValue(
                    "$taskId",
                    "01ARZ3NDEKTSV4RRFFQ69G5F82");
                command.Parameters.AddWithValue(
                    "$instructionId",
                    "01ARZ3NDEKTSV4RRFFQ69G5F83");
                command.Parameters.AddWithValue("$instruction", instruction);
                command.Parameters.AddWithValue(
                    "$instructionHash",
                    Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(instruction))));
                command.Parameters.AddWithValue("$occurredAt", occurredAt);
                var affected = await command.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
                return affected;
            },
            cancellationToken);

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
