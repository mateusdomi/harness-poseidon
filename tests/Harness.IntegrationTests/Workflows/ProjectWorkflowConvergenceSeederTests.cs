using System.Globalization;
using Harness.Host.Workflows;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Workflows;

/// <summary>
/// RN-02: prova de convergência em SQLite in-process. Sobre um tenant com um projeto SEM workflow
/// (como o "Poseidon", nascido antes da regra), o <see cref="ProjectWorkflowConvergenceSeeder"/>
/// vincula o template recomendado publicado — e é idempotente: reexecutar não reata nem duplica o
/// vínculo. Um projeto que já tem workflow é respeitado (não é revinculado).
/// </summary>
public sealed class ProjectWorkflowConvergenceSeederTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-24T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task BindsRecommendedWorkflowToWorkflowlessProjectAndIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"rn02-converge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "converge.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);

                var tenantId = UlidValue.New(Now).ToString();
                var profileId = UlidValue.New(Now.AddMilliseconds(1)).ToString();
                await InsertTenantAsync(dispatcher, tenantId, timeout.Token);
                await InsertProfileAsync(dispatcher, tenantId, profileId, timeout.Token);

                var organizations = new SqliteOrganizationStore(dispatcher);
                var projects = new SqliteProjectStore(dispatcher);
                var workflowCatalog = new SqliteWorkflowCatalogStore(dispatcher);
                var workflowAuthority = new SqliteWorkflowStore(dispatcher);

                var organizationId = UlidValue.New(Now.AddMilliseconds(2)).ToString();
                _ = await organizations.CreateAsync(
                    new OrganizationCreateCommand(
                        tenantId, organizationId, "Poseidon", "poseidon", "personal",
                        new OrganizationBrandRecord(null, null, null, null), Now),
                    timeout.Token);
                var projectId = UlidValue.New(Now.AddMilliseconds(3)).ToString();
                var chiefAgentId = UlidValue.New(Now.AddMilliseconds(4)).ToString();
                _ = await projects.CreateAsync(
                    new ProjectCreateCommand(
                        tenantId,
                        new ProjectRecord(
                            tenantId, projectId, organizationId, "Poseidon", "POS", "Control plane",
                            "active", "medium", null, "local", "develop",
                            [], new ProjectBrandRecord(null, null, null, null),
                            [profileId], 1, chiefAgentId, "manual", Now, Now, 0),
                        Now.AddMilliseconds(5)),
                    timeout.Token);

                var clock = new FixedClock(Now.AddMinutes(1));
                var workflowSeeder = new WorkflowTemplateSeeder(workflowAuthority, workflowCatalog, clock);
                var seeder = new ProjectWorkflowConvergenceSeeder(
                    projects, workflowCatalog, workflowSeeder, workflowAuthority, clock);

                // Antes: o projeto NÃO tem workflow — o chat mostraria "Nenhum workflow ativo".
                var before = await workflowCatalog.ListBindingsAsync(tenantId, projectId, null, 10, timeout.Token);
                Assert.Empty(before);

                // Primeira convergência: vincula o template recomendado.
                var boundFirst = await seeder.EnsureBoundAsync(tenantId, profileId, timeout.Token);
                Assert.Equal(1, boundFirst);

                var recommended = await ProjectWorkflowLinker.ResolveRecommendedAsync(
                    workflowCatalog, workflowSeeder, tenantId, timeout.Token);
                Assert.NotNull(recommended);
                var binding = Assert.Single(
                    await workflowCatalog.ListBindingsAsync(tenantId, projectId, null, 10, timeout.Token));
                Assert.Equal(recommended!.Id, binding.TemplateId);

                // Idempotência: reexecutar não reata nem duplica — o projeto com workflow é respeitado.
                var boundSecond = await seeder.EnsureBoundAsync(tenantId, profileId, timeout.Token);
                Assert.Equal(0, boundSecond);
                Assert.Single(
                    await workflowCatalog.ListBindingsAsync(tenantId, projectId, null, 10, timeout.Token));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task InsertTenantAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tenants(id,name,version,created_at) VALUES($id,$name,0,$at);";
            command.Parameters.AddWithValue("$id", tenantId);
            command.Parameters.AddWithValue("$name", "Tenant " + tenantId);
            command.Parameters.AddWithValue("$at", Store(Now));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private static async Task InsertProfileAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, string profileId, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO local_users(id,tenant_id,display_name,version,created_at) VALUES($id,$tenant,$name,0,$at);";
            command.Parameters.AddWithValue("$id", profileId);
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$name", "Operator");
            command.Parameters.AddWithValue("$at", Store(Now));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        private long _tick;
        public DateTimeOffset UtcNow => now.AddTicks(Interlocked.Increment(ref _tick));
    }
}
