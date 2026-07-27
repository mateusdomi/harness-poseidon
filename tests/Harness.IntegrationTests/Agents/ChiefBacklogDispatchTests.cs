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
using Harness.Modules.Providers.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Providers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Prova HONESTA e determinística do loop autônomo do Chefe (<see cref="ChiefBacklogLoopService"/>):
/// um card em <c>board_state='ready'</c> é despachado e movido para <c>development</c> — o caminho
/// dispatch→move real, contra o enum real, em UM ciclo do loop.
///
/// Cobre três defeitos que faziam o loop NUNCA despachar um card real (a "prova" anterior era oca —
/// semeava o card em `backlog` e assertava apenas <c>state != "Ready"</c>, trivialmente verdadeiro):
///   1. a query usava <c>"Ready"</c> (maiúsculo) contra o enum minúsculo case-sensitive → casava zero;
///   2. o move gravava <c>"Running"</c>, valor inexistente no CHECK (o válido é <c>development</c>);
///   3. o <c>WorkAttemptStartCommand</c> recebia o id da DEMANDA na posição do <c>InstructionVersionId</c>
///      → <c>StartAttemptAsync</c> retornava <c>InvalidState</c> sempre.
///
/// É determinístico e SEM cota: o repositório do projeto é um diretório comum (não-git), então o run
/// de fundo do orquestrador falha ao abrir a worktree DEPOIS do move; o move em si é síncrono ao
/// aceite do run e não depende do executor.
/// </summary>
public sealed class ChiefBacklogDispatchTests
{
    [Fact]
    public async Task ChiefLoopDispatchesReadyCardAndMovesItToDevelopment()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var root = Path.Combine(AppContext.BaseDirectory, "chief-dispatch", Guid.NewGuid().ToString("N"));
        var controlledRoot = Path.Combine(root, "controlled");
        var repo = Path.Combine(controlledRoot, "repo"); // diretório comum (não-git) => run de fundo falha antes de spawnar CLI
        Directory.CreateDirectory(repo);
        var db = Path.Combine(root, "chief.db");
        var accountsFile = Path.Combine(root, "no-accounts.json"); // inexistente => contas reais de ~/.harness NÃO são carregadas
        var profilesRoot = Path.Combine(root, "profiles");
        var primaryAlias = "test-primary-" + Guid.NewGuid().ToString("N");
        var fallbackAlias = "test-fallback-" + Guid.NewGuid().ToString("N");

        await using var app = HostApplication.Build(
        [
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", db,
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:AgentRuns:ControlledRoot", controlledRoot,
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
            // Uma conta backend DISPONÍVEL (os defaults canônicos carregam como AuthenticationRequired,
            // que o scheduler ignora). Os path scopes precisam bater exatamente com os claims do card.
            var accountRegistry = app.Services.GetRequiredService<AgentAccountRegistry>();
            accountRegistry.Register(new AgentAccountContract(
                primaryAlias, "zhipu", ExecutorCatalog.Glm,
                "keychain://poseidon/" + primaryAlias, "confighome://" + primaryAlias,
                new[] { AgentRoles.BackendSpecialist },
                AgentRoles.PathScopesFor(AgentRoles.BackendSpecialist),
                AgentAccountState.Available, AgentAccountHealth.Healthy,
                ConcurrencyLimit: 1, ActiveAttempts: 0, CurrentAttemptId: null, Quota: null,
                CooldownUntil: null, LastSuccessfulSmokeAt: null, FailureReason: null, Priority: 200));
            accountRegistry.Register(new AgentAccountContract(
                fallbackAlias, "zhipu", ExecutorCatalog.Glm,
                "keychain://poseidon/" + fallbackAlias, "confighome://" + fallbackAlias,
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

            // Bootstrap da sessão de perfil local (o middleware adota o perfil e grava o cookie de sessão).
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
            var taskId = await PostId(client, "/api/v1/tasks",
                new CreateTaskRequest(projectId, "Implementar service backend", "Adicione um método em src/.", demId), cts.Token);

            var board = app.Services.GetRequiredService<IWorkBoardStore>();
            var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>().ListAsync(cts.Token))[0].TenantId;

            // Fato persistido pelo caminho real do executor: a conta primária esgotou cota.
            // O ProviderQuotaCollector deve ler este outcome, alimentar o Capacity Manager e
            // fazer o scheduler autoritativo escolher a conta de fallback.
            var now = DateTimeOffset.UtcNow;
            await app.Services.GetRequiredService<IModelInvocationStore>().RecordInvocationAsync(
                new ModelInvocationRecord(
                    UlidValue.New(now).ToString(),
                    tenantId,
                    projectId,
                    taskId,
                    UlidValue.New(now).ToString(),
                    "zhipu",
                    string.Empty,
                    primaryAlias,
                    0,
                    0,
                    0m,
                    100,
                    "quotaexhausted|usage_unknown",
                    now),
                cts.Token);

            // Triagem: backlog -> ready. O loop só enxerga board_state == "ready".
            await board.MoveTaskAsync(
                new BoardTaskMoveCommand(tenantId, taskId, "ready", null, "human", DateTimeOffset.UtcNow), cts.Token);

            // UM ciclo do loop do Chefe, disparado deterministicamente.
            var service = app.Services.GetServices<IHostedService>().OfType<ChiefBacklogLoopService>().Single();
            var (dispatched, deferred) = await service.RunCycleAsync(cts.Token);

            // A contagem GLOBAL do ciclo deixou de ser exata: o mesmo ciclo também conduz a esteira
            // do projeto e pode criar o card do artefato da fase ativa. O que este teste prova é o
            // despacho DESTE card — asserido logo abaixo pelo estado e pela tentativa durável.
            Assert.True(dispatched >= 1,
                $"esperava ao menos um despacho; obtido dispatched={dispatched}, deferred={deferred}");

            // PROVA: o card foi movido de `ready` para `development` pelo loop do Chefe.
            var moved = await board.GetTaskAsync(tenantId, taskId, cts.Token);
            Assert.Equal("development", moved!.State);

            // O move só ocorre após uma tentativa durável iniciar — prova que uma existe.
            var attempts = await board.ListAttemptsAsync(tenantId, taskId, null, 10, cts.Token);
            var attempt = Assert.Single(attempts);
            Assert.Equal(fallbackAlias, attempt.AgentId);

            // A mesma decisão que alimentou a tentativa foi registrada no ledger append-only.
            var audit = await app.Services.GetRequiredService<IAuditEventStore>().ListAsync(
                new AuditEventQuery(
                    tenantId,
                    null,
                    20,
                    Action: "model.routing_decided",
                    TargetType: "task",
                    TargetId: taskId),
                cts.Token);
            var routing = Assert.Single(audit);
            using var detail = JsonDocument.Parse(routing.Detail!);
            Assert.Equal(
                fallbackAlias,
                detail.RootElement.GetProperty("accountAlias").GetString());
            Assert.Equal("zhipu", detail.RootElement.GetProperty("provider").GetString());
            Assert.True(detail.RootElement.GetProperty("isFallback").GetBoolean());
            Assert.Equal(
                "model_router.routed_to_fallback",
                detail.RootElement.GetProperty("decisionReason").GetString());
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

    private static async Task<string> PostId<T>(HttpClient client, string route, T body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(route, body, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
