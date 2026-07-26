using System.Globalization;
using Harness.Host.Projects;
using Harness.Host.Workflows;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Projects;

/// <summary>
/// RN-03: prova de convergência de ESTADO em SQLite in-process. Sobre o projeto "Poseidon" (id fixo)
/// já com workflow vinculado mas SEM run, sem protótipo e com a organização sem marca, o
/// <see cref="ProjectStateConvergenceSeeder"/>:
///  - cria e INICIA a run real do binding (fase ativa, "running" — em andamento), sem fabricar
///    progresso (nenhum objetivo tica sozinho);
///  - registra o front atual como protótipo em "ready" (sem URL pública fabricada);
///  - preenche a marca default da organização quando vazia.
/// E é idempotente: reexecutar não cria segunda run, segundo protótipo nem reescreve a marca.
/// </summary>
public sealed class ProjectStateConvergenceSeederTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-24T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task ConvergesRealStateAndIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"rn03-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "state.db"));
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
                var prototypes = new SqlitePrototypeStore(dispatcher);

                var organizationId = UlidValue.New(Now.AddMilliseconds(2)).ToString();
                _ = await organizations.CreateAsync(
                    new OrganizationCreateCommand(
                        tenantId, organizationId, "Grupo Poseidon", "grupo-poseidon", "personal",
                        new OrganizationBrandRecord(null, null, null, null), Now),
                    timeout.Token);

                // Projeto com o id FIXO do Poseidon, sem marca própria e com prototipagem habilitada.
                var chiefAgentId = UlidValue.New(Now.AddMilliseconds(4)).ToString();
                _ = await projects.CreateAsync(
                    new ProjectCreateCommand(
                        tenantId,
                        new ProjectRecord(
                            tenantId, ProjectStateConvergenceSeeder.PoseidonProjectId, organizationId,
                            "Poseidon", "POS", "Control plane", "active", "medium", null, "local", "develop",
                            [], new ProjectBrandRecord(null, null, null, null),
                            [profileId], 1, chiefAgentId, "manual", Now, Now, 0),
                        Now.AddMilliseconds(5)),
                    timeout.Token);

                var clock = new FixedClock(Now.AddMinutes(1));
                var workflowSeeder = new WorkflowTemplateSeeder(workflowAuthority, workflowCatalog, clock);

                // Pré-condição RN-02: vincula o workflow recomendado (cria binding + definição real).
                var workflowConvergence = new ProjectWorkflowConvergenceSeeder(
                    projects, workflowCatalog, workflowSeeder, workflowAuthority, clock);
                Assert.Equal(1, await workflowConvergence.EnsureBoundAsync(tenantId, profileId, timeout.Token));
                var binding = Assert.Single(
                    await workflowCatalog.ListBindingsAsync(
                        tenantId, ProjectStateConvergenceSeeder.PoseidonProjectId, null, 10, timeout.Token));

                // Antes: nenhuma run (cockpit mostraria "Nenhuma execução / Fase: nenhuma"), sem
                // protótipo, org sem marca.
                Assert.Empty(await workflowCatalog.ListRunsAsync(tenantId, binding.Id, null, 10, timeout.Token));
                Assert.Empty(await prototypes.ListPrototypesAsync(
                    tenantId, ProjectStateConvergenceSeeder.PoseidonProjectId, null, 10, timeout.Token));

                var seeder = new ProjectStateConvergenceSeeder(
                    projects, workflowCatalog, workflowAuthority, prototypes, organizations, clock);

                // Primeira convergência: run iniciada, protótipo registrado, marca preenchida.
                var first = await seeder.EnsureConvergedAsync(tenantId, profileId, timeout.Token);
                Assert.True(first.RunStarted);
                Assert.True(first.PrototypeRegistered);
                Assert.True(first.BrandFilled);

                // Fase honesta: existe uma run "running" (em andamento) com uma fase ATIVA.
                var run = Assert.Single(
                    await workflowCatalog.ListRunsAsync(tenantId, binding.Id, null, 10, timeout.Token));
                Assert.Equal("running", run.State);
                var phases = await workflowCatalog.ListPhasesAsync(tenantId, run.Id, null, 50, timeout.Token);
                Assert.Contains(phases, phase => phase.State == "active");

                // Progresso derivado é honesto e baixo (run recém-iniciada), NUNCA 100% fabricado.
                var aggregate = await workflowAuthority.ReadRunAggregateAsync(tenantId, run.Id, timeout.Token);
                Assert.NotNull(aggregate);
                Assert.Equal(0m, aggregate!.Approved);

                // Protótipo do front em "ready", sem URL pública fabricada.
                var prototype = Assert.Single(
                    await prototypes.ListPrototypesAsync(
                        tenantId, ProjectStateConvergenceSeeder.PoseidonProjectId, null, 10, timeout.Token));
                Assert.Equal(ProjectStateConvergenceSeeder.FrontPrototypeId, prototype.Id);
                Assert.Equal("Front atual do Poseidon", prototype.Name);
                Assert.Equal("ready", prototype.State);
                Assert.Null(prototype.Url);

                // Marca preenchida (plausível, sem logo fabricado).
                var organization = await organizations.GetAsync(tenantId, organizationId, timeout.Token);
                Assert.NotNull(organization);
                Assert.False(string.IsNullOrWhiteSpace(organization!.Brand.PrimaryColor));
                Assert.Null(organization.Brand.LogoUrl);

                // Idempotência: reexecutar não muda nada e não duplica.
                var second = await seeder.EnsureConvergedAsync(tenantId, profileId, timeout.Token);
                Assert.False(second.ChangedAnything);
                Assert.Single(await workflowCatalog.ListRunsAsync(tenantId, binding.Id, null, 10, timeout.Token));
                Assert.Single(await prototypes.ListPrototypesAsync(
                    tenantId, ProjectStateConvergenceSeeder.PoseidonProjectId, null, 10, timeout.Token));
                var reread = await organizations.GetAsync(tenantId, organizationId, timeout.Token);
                Assert.Equal(organization.Version, reread!.Version);
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

    [Fact]
    public async Task IsNoOpWhenPoseidonProjectIsAbsent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"rn03-absent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "absent.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                var tenantId = UlidValue.New(Now).ToString();
                var profileId = UlidValue.New(Now.AddMilliseconds(1)).ToString();
                await InsertTenantAsync(dispatcher, tenantId, timeout.Token);
                await InsertProfileAsync(dispatcher, tenantId, profileId, timeout.Token);

                var clock = new FixedClock(Now.AddMinutes(1));
                var seeder = new ProjectStateConvergenceSeeder(
                    new SqliteProjectStore(dispatcher),
                    new SqliteWorkflowCatalogStore(dispatcher),
                    new SqliteWorkflowStore(dispatcher),
                    new SqlitePrototypeStore(dispatcher),
                    new SqliteOrganizationStore(dispatcher),
                    clock);

                var result = await seeder.EnsureConvergedAsync(tenantId, profileId, timeout.Token);
                Assert.False(result.ChangedAnything);
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
