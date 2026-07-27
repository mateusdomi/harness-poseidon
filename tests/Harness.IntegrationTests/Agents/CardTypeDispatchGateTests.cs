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
using Harness.Persistence.Abstractions.WorkChain;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Prova determinística do GATE de card_type + Definition of Ready no loop autônomo do Chefe
/// (<see cref="ChiefBacklogLoopService"/>): apenas cards <c>card_type='agent_task'</c> que passam a
/// DoR são despachados. Um card <c>human_gate</c> em <c>board_state='ready'</c> é IGNORADO por um
/// ciclo (dispatched==0) e PERMANECE em `ready` — o auto-dispatch nunca executa um gate humano.
/// Um card <c>agent_task</c> equivalente É despachado (dispatched==1 -> `development`).
///
/// Reusa o mesmo harness honesto do <see cref="ChiefBacklogDispatchTests"/>: repositório do projeto
/// é um diretório comum (não-git), então o run de fundo falha ao abrir a worktree DEPOIS do move;
/// o move é síncrono ao aceite do run e não depende do executor. Sem cota, sem contas reais.
/// </summary>
public sealed class CardTypeDispatchGateTests
{
    [Fact]
    public async Task HumanGateCardIsNotDispatchedAndStaysReady()
    {
        var result = await RunSingleCardCycleAsync("human_gate");

        // A contagem GLOBAL do ciclo deixou de ser exata: o mesmo ciclo conduz a esteira e pode
        // criar/despachar o card do artefato da fase ativa. A prova do gate humano é ESTE card não
        // ter tentativa e permanecer em `ready` — o que nenhum outro card do ciclo pode falsear.
        Assert.Equal("ready", result.FinalState);
        Assert.Empty(result.Attempts);
    }

    [Fact]
    public async Task AgentTaskCardIsDispatchedToDevelopment()
    {
        var result = await RunSingleCardCycleAsync("agent_task");

        Assert.True(result.Dispatched >= 1, $"esperava ao menos um despacho; obtido {result.Dispatched}");
        Assert.Equal("development", result.FinalState);
        Assert.NotEmpty(result.Attempts);
    }

    private static async Task<CycleResult> RunSingleCardCycleAsync(string cardType)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var root = Path.Combine(AppContext.BaseDirectory, "card-type-gate", Guid.NewGuid().ToString("N"));
        var controlledRoot = Path.Combine(root, "controlled");
        var repo = Path.Combine(controlledRoot, "repo"); // diretório comum (não-git) => run de fundo falha antes de spawnar CLI
        Directory.CreateDirectory(repo);
        var db = Path.Combine(root, "chief.db");
        var accountsFile = Path.Combine(root, "no-accounts.json"); // inexistente => contas reais de ~/.harness NÃO são carregadas
        var profilesRoot = Path.Combine(root, "profiles");
        var alias = "test-backend-" + Guid.NewGuid().ToString("N"); // alias único => sem estado residual no ledger

        await using var app = HostApplication.Build(
        [
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", db,
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:AgentRuns:ControlledRoot", controlledRoot,
            // Ledger de disponibilidade PRÓPRIO do teste: sem isto o Host de teste lê e escreve
            // o ledger da instalação real do operador — uma conta em cooldown na máquina
            // reprovava a suíte, e o teste sujava o estado de produção do dono.
            "--Harness:AgentRuns:AvailabilityLedgerPath",
                Path.Combine(Path.GetTempPath(), $"harness-availability-{Guid.NewGuid():N}.json"),
            "--Harness:AgentRuns:ProfilesRoot", profilesRoot,
            "--Harness:AgentRuns:AccountsFilePath", accountsFile,
            "--Harness:AgentRuns:AutoDispatchEnabled", "false", // o ciclo é disparado pelo teste, não pelo BackgroundService
            "--Harness:AgentRuns:AutoDispatchMaxConcurrent", "1",
            "--Harness:AgentRuns:RunTimeout", "00:00:30",
            "--Harness:AgentRuns:LeaseDuration", "00:05:00",
        ]);

        await app.StartAsync(cts.Token);
        try
        {
            app.Services.GetRequiredService<AgentAccountRegistry>().Register(new AgentAccountContract(
                alias, "zhipu", ExecutorCatalog.Glm,
                "keychain://poseidon/" + alias, "confighome://" + alias,
                new[] { AgentRoles.BackendSpecialist },
                AgentRoles.PathScopesFor(AgentRoles.BackendSpecialist),
                AgentAccountState.Available, AgentAccountHealth.Healthy,
                ConcurrencyLimit: 1, ActiveAttempts: 0, CurrentAttemptId: null, Quota: null,
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
            var projectId = await PostId(client, "/api/v1/projects",
                new CreateProjectRequest
                {
                    OrganizationId = orgId,
                    Name = "Poseidon",
                    Key = "PSD",
                    Description = "Control plane",
                    RepositoryUrl = Path.GetFullPath(repo),
                }, cts.Token);
            var solId = await PostId(client, "/api/v1/solicitations",
                new CreateSolicitationRequest(projectId, "request", "Probe", "Criar um probe backend."), cts.Token);
            var demId = await PostId(client, "/api/v1/demands",
                new CreateDemandRequest(projectId, "Probe backend", "Implementar no src.", solId), cts.Token);

            // O card carrega o card_type sob teste (CreateTaskRequest.CardType é entrada, fora da
            // resposta pública). Só 'agent_task' é auto-despachável.
            var taskId = await PostId(client, "/api/v1/tasks",
                new CreateTaskRequest(projectId, "Implementar service backend", "Adicione um método em src/.",
                    demId, CardType: cardType), cts.Token);

            var board = app.Services.GetRequiredService<IWorkBoardStore>();
            var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>().ListAsync(cts.Token))[0].TenantId;

            // Triagem: backlog -> ready. O loop só enxerga board_state == "ready".
            await board.MoveTaskAsync(
                new BoardTaskMoveCommand(tenantId, taskId, "ready", null, "human", DateTimeOffset.UtcNow), cts.Token);

            var service = app.Services.GetServices<IHostedService>().OfType<ChiefBacklogLoopService>().Single();
            var (dispatched, deferred) = await service.RunCycleAsync(cts.Token);

            var final = await board.GetTaskAsync(tenantId, taskId, cts.Token);
            var attempts = await board.ListAttemptsAsync(tenantId, taskId, null, 10, cts.Token);
            return new CycleResult(dispatched, deferred, final!.State, attempts);
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

    private sealed record CycleResult(
        int Dispatched, int Deferred, string FinalState, IReadOnlyList<BoardAttemptRecord> Attempts);

    private static async Task<string> PostId<T>(HttpClient client, string route, T body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(route, body, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
