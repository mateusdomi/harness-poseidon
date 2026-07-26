using System.Globalization;
using Harness.Host.Providers;
using Harness.Host.Readiness;
using Harness.Host.Workflows;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Providers;

/// <summary>
/// GP-06 (fecho): prova de fiação end-to-end em SQLite in-process. Sobre um tenant com projeto e
/// chefe reais, o <see cref="ChiefCliProviderCatalogSeeder"/> converge o catálogo para o estado
/// EXECUTÁVEL (conta+modelo reais ativos, chefe roteado, workflow vinculado), de forma que o read
/// model de prontidão reporte <c>ExecutionReady = Ready</c> (não Simulated) — e é idempotente:
/// reexecutar não duplica conta/modelo nem reata o vínculo.
/// </summary>
public sealed class ChiefCliProviderCatalogSeederTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-23T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task SeedsRealCatalogUntilExecutionReadyAndIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"gp06-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "seed.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);

                var tenantId = UlidValue.New(Now).ToString();
                var profileId = UlidValue.New(Now.AddMilliseconds(1)).ToString();
                await InsertTenantAsync(dispatcher, tenantId, timeout.Token);
                await InsertProfileAsync(dispatcher, tenantId, profileId, timeout.Token);

                var organizations = new SqliteOrganizationStore(dispatcher);
                var projects = new SqliteProjectStore(dispatcher);
                var providers = new SqliteProviderCatalogStore(dispatcher);
                var agents = new SqliteAgentCatalogStore(dispatcher);
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
                var seeder = new ChiefCliProviderCatalogSeeder(
                    providers, agents, projects, workflowCatalog, workflowSeeder, workflowAuthority, clock);
                var readiness = new ProjectReadinessService(
                    organizations, providers, agents, workflowCatalog);

                // Antes do seed: a execução está BLOQUEADA (sem conta/modelo/workflow reais).
                var project = await projects.GetAsync(tenantId, projectId, timeout.Token);
                Assert.NotNull(project);
                var before = await readiness.EvaluateAsync(tenantId, project!, true, timeout.Token);
                Assert.NotEqual(ConfigurationState.Ready, ExecutionState(before));

                // Primeiro seed: cria conta+modelo reais, ativa/habilita, roteia o chefe e vincula
                // o workflow — tudo de uma vez.
                var first = await seeder.EnsureSeededAsync(tenantId, profileId, timeout.Token);
                Assert.True(first.AccountCreated);
                Assert.True(first.AccountActivated);
                Assert.True(first.ModelCreated);
                Assert.True(first.ModelEnabled);
                Assert.Equal(1, first.AgentsWired);
                Assert.Equal(1, first.WorkflowsBound);

                // O read model agora reporta execução PRONTA e REAL (não Simulated). O agregado
                // geral maximiza em "Configured" — a etapa de conta de provider tem esse como estado
                // terminal saudável — então basta que o overall não seja rebaixado a Simulated/vazio.
                var after = await readiness.EvaluateAsync(tenantId, project!, true, timeout.Token);
                Assert.Equal(ConfigurationState.Ready, ExecutionState(after));
                Assert.True(
                    after.OverallState is ConfigurationState.Configured or ConfigurationState.Ready,
                    $"Overall readiness regressed to {after.OverallState}.");
                Assert.Empty(ExecutionStep(after).Blockers);

                // A conta e o modelo semeados são REAIS (fora das faixas simuladas).
                var account = await providers.GetAccountAsync(
                    tenantId, ChiefCliProviderCatalogSeeder.ChiefAccountId, timeout.Token);
                Assert.NotNull(account);
                Assert.Equal("active", account!.State);
                var model = await providers.GetModelAsync(
                    tenantId, ChiefCliProviderCatalogSeeder.ChiefModelId, timeout.Token);
                Assert.NotNull(model);
                Assert.True(model!.Enabled);
                Assert.Contains("chat", model.Capabilities);
                Assert.Equal(ChiefCliProviderCatalogSeeder.ChiefModelName, model.Name);

                // Idempotência: reexecutar NÃO cria/ativa/habilita/reata nada de novo.
                var second = await seeder.EnsureSeededAsync(tenantId, profileId, timeout.Token);
                Assert.False(second.ChangedAnything);
                Assert.False(second.AccountCreated);
                Assert.False(second.AccountActivated);
                Assert.False(second.ModelCreated);
                Assert.False(second.ModelEnabled);
                Assert.Equal(0, second.AgentsWired);
                Assert.Equal(0, second.WorkflowsBound);

                // E não duplicou linhas: uma única conta e um único modelo (mais nada além do que
                // o catálogo auto-semeia como TIPOS de provider).
                var accounts = await providers.ListAccountsAsync(tenantId, null, 200, timeout.Token);
                Assert.Single(accounts);
                var models = await providers.ListModelsAsync(tenantId, null, 200, timeout.Token);
                Assert.Single(models);
                var bindings = await workflowCatalog.ListBindingsAsync(tenantId, projectId, null, 10, timeout.Token);
                Assert.Single(bindings);

                // A prontidão permanece PRONTA após a reexecução.
                var stable = await readiness.EvaluateAsync(tenantId, project!, true, timeout.Token);
                Assert.Equal(ConfigurationState.Ready, ExecutionState(stable));
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

    private static ReadinessStepContract ExecutionStep(ProjectReadinessSnapshot snapshot) =>
        snapshot.Steps.Single(step => step.Step == ReadinessStep.ExecutionReady);

    private static ConfigurationState ExecutionState(ProjectReadinessSnapshot snapshot) =>
        ExecutionStep(snapshot).State;

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
