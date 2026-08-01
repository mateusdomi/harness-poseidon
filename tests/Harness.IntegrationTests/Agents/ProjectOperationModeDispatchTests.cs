using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Fase 1E — a AUTONOMIA é decisão de CADA PROJETO, não um interruptor único da instalação.
///
/// Prova as duas metades no MESMO host e no MESMO ciclo, que é o que torna a prova honesta: um
/// ciclo que não despacha nada passaria trivialmente na metade negativa.
///   * projeto AUTÔNOMO — o card em `ready` é despachado e vai para `development` sem nenhum
///     toggle manual, que é o aceite "instalação limpa conduz a demanda ponta a ponta";
///   * projeto MANUAL — o card equivalente PERMANECE em `ready`; o disparo é humano.
///
/// O modo do projeto manual é gravado pelo caminho REAL do produto (<c>SetOperationModeAsync</c>,
/// a mesma mutação que a tela do dono usa, com aceite de risco registrado) — e não por um campo
/// que ninguém atualiza depois da criação.
/// </summary>
public sealed class ProjectOperationModeDispatchTests
{
    [Fact]
    public async Task ManualProjectIsNotDispatchedWhileAutonomousProjectIs()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var root = Path.Combine(AppContext.BaseDirectory, "mode-dispatch", Guid.NewGuid().ToString("N"));
        var controlledRoot = Path.Combine(root, "controlled");
        var autonomousRepo = Path.Combine(controlledRoot, "autonomous-repo");
        var manualRepo = Path.Combine(controlledRoot, "manual-repo");
        Directory.CreateDirectory(autonomousRepo);
        Directory.CreateDirectory(manualRepo);
        var db = Path.Combine(root, "mode.db");
        var accountsFile = Path.Combine(root, "no-accounts.json");
        var alias = "test-mode-" + Guid.NewGuid().ToString("N");

        await using var app = HostApplication.Build(
        [
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", db,
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:IsolatedExecution:Mode", "Fake",
            "--Harness:AgentRuns:ControlledRoot", controlledRoot,
            "--Harness:AgentRuns:AvailabilityLedgerPath",
                Path.Combine(Path.GetTempPath(), $"harness-availability-{Guid.NewGuid():N}.json"),
            "--Harness:AgentRuns:ProfilesRoot", Path.Combine(root, "profiles"),
            "--Harness:AgentRuns:AccountsFilePath", accountsFile,
            "--Harness:AgentRuns:AutoDispatchEnabled", "false", // o ciclo é disparado pelo teste
            "--Harness:AgentRuns:AutoDispatchMaxConcurrent", "2",
            "--Harness:AgentRuns:RunTimeout", "00:00:30",
            "--Harness:AgentRuns:LeaseDuration", "00:05:00",
        ]);

        await app.StartAsync(cts.Token);
        try
        {
            var accountRegistry = app.Services.GetRequiredService<AgentAccountRegistry>();
            accountRegistry.Register(new AgentAccountContract(
                alias, "zhipu", ExecutorCatalog.Glm,
                "keychain://poseidon/" + alias, "confighome://" + alias,
                new[] { AgentRoles.BackendSpecialist },
                AgentRoles.PathScopesFor(AgentRoles.BackendSpecialist),
                AgentAccountState.Available, AgentAccountHealth.Healthy,
                ConcurrencyLimit: 2, ActiveAttempts: 0, CurrentAttemptId: null, Quota: null,
                CooldownUntil: null, LastSuccessfulSmokeAt: null, FailureReason: null, Priority: 100));

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(addresses.Single(a => a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))),
            };

            (await client.PostAsJsonAsync("/api/v1/profiles",
                new CreateProfileRequest("Operador", null, null, "pt-BR"), cts.Token)).EnsureSuccessStatusCode();
            (await client.GetAsync("/api/v1/profiles/current", cts.Token)).EnsureSuccessStatusCode();

            var orgId = await PostId(client, "/api/v1/organizations",
                new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, cts.Token);

            var autonomousId = await CreateProjectAsync(client, orgId, "Autonomo", "AUT", autonomousRepo, cts.Token);
            var manualId = await CreateProjectAsync(client, orgId, "Manual", "MAN", manualRepo, cts.Token);

            var board = app.Services.GetRequiredService<IWorkBoardStore>();
            var workflows = app.Services.GetRequiredService<IWorkflowCatalogStore>();
            var ownerProfile = (await app.Services.GetRequiredService<ILocalProfileStore>().ListAsync(cts.Token))[0];
            var tenantId = ownerProfile.TenantId;

            // O projeto nasce autônomo; o dono põe ESTE em manual pelo caminho real do produto.
            var binding = Assert.Single(await workflows.ListBindingsAsync(tenantId, manualId, null, 2, cts.Token));
            Assert.Equal("autonomous", binding.OperationMode); // herdado do projeto: vincular não torna manual
            var now = DateTimeOffset.UtcNow;
            await workflows.SetOperationModeAsync(
                new WorkflowOperationModeCommand(
                    tenantId, binding.Id, "manual", [],
                    UlidValue.New(now).ToString(), ownerProfile.Id,
                    "O dono quer conduzir este projeto card a card.", now),
                cts.Token);

            var autonomousTask = await SeedReadyCardAsync(client, board, tenantId, autonomousId, cts.Token);
            var manualTask = await SeedReadyCardAsync(client, board, tenantId, manualId, cts.Token);

            var service = app.Services.GetServices<IHostedService>().OfType<ChiefBacklogLoopService>().Single();
            await service.RunCycleAsync(cts.Token);

            // PROVA POSITIVA: sem nenhum toggle, o projeto autônomo andou sozinho.
            //
            // A âncora é a TENTATIVA DURÁVEL, não o board_state. O repositório do teste é um
            // diretório comum (não-git), então o run de fundo falha logo depois do despacho e pode
            // mover o card de novo; assertar `development` é uma corrida contra esse run — foi
            // exatamente assim que este teste falhou uma vez sob carga. A tentativa, ao contrário,
            // é o fato append-only que só existe porque o laço despachou.
            var autonomousAttempts = await board.ListAttemptsAsync(tenantId, autonomousTask, null, 5, cts.Token);
            var autonomousAttempt = Assert.Single(autonomousAttempts);
            Assert.True(UlidValue.TryParse(autonomousAttempt.AgentId, out _));
            var professional = await app.Services.GetRequiredService<IAgentCatalogStore>()
                .GetAgentAsync(tenantId, autonomousAttempt.AgentId, cts.Token);
            Assert.NotNull(professional);
            Assert.Equal(autonomousId, professional!.ProjectId);

            // PROVA NEGATIVA: no MESMO ciclo, o projeto manual não andou — e não por falta de
            // card pronto, conta disponível ou escopo, que o projeto autônomo acabou de exercer.
            Assert.Empty(await board.ListAttemptsAsync(tenantId, manualTask, null, 5, cts.Token));
            var manualCard = await board.GetTaskAsync(tenantId, manualTask, cts.Token);
            Assert.Equal("ready", manualCard!.State);
        }
        finally
        {
            await app.StopAsync(cts.Token);
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // limpeza best-effort
            }
        }
    }

    private static Task<string> CreateProjectAsync(
        HttpClient client, string orgId, string name, string key, string repo, CancellationToken ct) =>
        PostId(client, "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = orgId,
                Name = name,
                Key = key,
                Description = "Control plane",
                RepositoryUrl = Path.GetFullPath(repo),
            }, ct);

    private static async Task<string> SeedReadyCardAsync(
        HttpClient client, IWorkBoardStore board, string tenantId, string projectId, CancellationToken ct)
    {
        var solId = await PostId(client, "/api/v1/solicitations",
            new CreateSolicitationRequest(projectId, "request", "Probe", "Criar um probe backend."), ct);
        var demId = await PostId(client, "/api/v1/demands",
            new CreateDemandRequest(projectId, "Probe backend", "Implementar no src.", solId), ct);
        var taskId = await PostId(client, "/api/v1/tasks",
            new CreateTaskRequest(projectId, "Implementar service backend", "Adicione um método em src/.", demId), ct);
        await board.MoveTaskAsync(
            new BoardTaskMoveCommand(tenantId, taskId, "ready", null, "human", DateTimeOffset.UtcNow), ct);
        return taskId;
    }

    private static async Task<string> PostId<T>(HttpClient client, string route, T body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(route, body, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
