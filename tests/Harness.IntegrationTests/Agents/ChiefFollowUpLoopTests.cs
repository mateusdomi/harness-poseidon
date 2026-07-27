using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Prova PONTA A PONTA (determinística, sem CLI externa e sem cota) do ciclo de acompanhamento
/// do chefe — os elos que fecham "um agente termina → outro revisa → o humano integra → o
/// próximo card entra":
///   1. plano materializado nasce em `backlog` e a TRIAGEM POR ONDAS promove só a onda 1
///      (dependências não entregues seguram a onda 2);
///   2. a COLHEITA converte um run externo Completed em `awaiting_review` durável (board `review`);
///   3. um veredito REAL de review é aplicado na cadeia (ator≠crítico); veredito de
///      infraestrutura NUNCA reprova o trabalho;
///   4. o GATE HUMANO de merge faz o merge git REAL (--no-ff) e conclui o card (`done`);
///   5. com a dependência entregue, a triagem promove a onda 2 — o "puxar o próximo card".
/// </summary>
public sealed class ChiefFollowUpLoopTests
{
    [Fact]
    public async Task FollowUpLoopClosesTheDeliveryCycleAcrossWaves()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var root = Path.Combine(AppContext.BaseDirectory, "chief-followup", Guid.NewGuid().ToString("N"));
        var controlledRoot = Path.Combine(root, "controlled");
        var repo = Path.Combine(controlledRoot, "repo");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Piloto\n", cts.Token);
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "chore: base");

        var db = Path.Combine(root, "followup.db");
        var accountsFile = Path.Combine(root, "no-accounts.json");
        var profilesRoot = Path.Combine(root, "profiles");
        var workerAlias = "test-worker-" + Guid.NewGuid().ToString("N");

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
            "--Harness:AgentRuns:AutoDispatchEnabled", "false",
            "--Harness:AgentRuns:AutoDispatchMaxConcurrent", "1",
            "--Harness:AgentRuns:RunTimeout", "00:00:30",
            "--Harness:AgentRuns:LeaseDuration", "00:05:00",
        ]);

        await app.StartAsync(cts.Token);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(addresses.Single(a =>
                    a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))),
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
                    Name = "Agenda",
                    Key = "AGD",
                    Description = "Piloto do ciclo de entrega.",
                    RepositoryUrl = Path.GetFullPath(repo),
                }, cts.Token);
            var solId = await PostId(client, "/api/v1/solicitations",
                new CreateSolicitationRequest(projectId, "request", "AGD-01", "Criar utilitário de agenda."), cts.Token);
            var demId = await PostId(client, "/api/v1/demands",
                new CreateDemandRequest(projectId, "AGD-01 Utilitário de agenda",
                    "Investigar a abordagem e implementar um utilitário de agenda no src.", solId), cts.Token);

            // 1. Plano determinístico (spike T01 + backend T02, que DEPENDE de T01) e
            //    materialização em cards reais — ambos nascem em `backlog`. A onda existe porque a
            //    implementação espera a incerteza ser resolvida, não por um gate de cerimônia.
            using (var planResponse = await client.PostAsJsonAsync(
                $"/api/v1/demands/{demId}/plan", new { }, cts.Token))
            {
                Assert.Equal(HttpStatusCode.Created, planResponse.StatusCode);
            }

            using (var materialize = await client.PostAsJsonAsync(
                $"/api/v1/demands/{demId}/plan/materialize", new { }, cts.Token))
            {
                Assert.Equal(HttpStatusCode.OK, materialize.StatusCode);
            }

            var board = app.Services.GetRequiredService<IWorkBoardStore>();
            var chain = app.Services.GetRequiredService<IWorkChainStore>();
            var plans = app.Services.GetRequiredService<IDemandPlanStore>();
            var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                .ListAsync(cts.Token))[0].TenantId;
            var project = (await app.Services.GetRequiredService<IProjectStore>()
                .GetAsync(tenantId, projectId, cts.Token))!;

            var page = await board.PageTasksAsync(
                tenantId,
                new BoardTaskPageQuery(projectId, demId, null, null, null, null, "active", null, 0, 10),
                cts.Token);
            Assert.Equal(2, page.Items.Count);
            var wave1 = page.Items.Single(task => task.Title.Contains("/T01", StringComparison.Ordinal));
            var wave2 = page.Items.Single(task => task.Title.Contains("/T02", StringComparison.Ordinal));
            Assert.All(page.Items, task => Assert.Equal("backlog", task.State));

            var service = app.Services.GetServices<IHostedService>()
                .OfType<ChiefBacklogLoopService>().Single();

            // 2. TRIAGEM POR ONDAS: só T01 (sem dependências) sobe; T02 espera a entrega de T01.
            var promoted = await service.PromotePlannedCardsAsync(
                tenantId, project, board, plans, cts.Token);
            Assert.Equal(1, promoted);
            Assert.Equal("ready", (await board.GetTaskAsync(tenantId, wave1.Id, cts.Token))!.State);
            Assert.Equal("backlog", (await board.GetTaskAsync(tenantId, wave2.Id, cts.Token))!.State);

            // 3. Desenvolvimento simulado SEM executor externo: tentativa durável real
            //    (assign+start na cadeia) + trabalho commitado na branch da tentativa + workspace
            //    durável levado a Completed — exatamente o rastro que um run real deixa.
            var task1 = (await board.GetTaskAsync(tenantId, wave1.Id, cts.Token))!;
            var instructions = await board.ListInstructionsAsync(tenantId, task1.Id, null, 10, cts.Token);
            var now = DateTimeOffset.UtcNow;
            var attemptId = UlidValue.New(now).ToString();
            var assigned = await chain.AssignTaskAsync(
                new WorkTaskAssignmentCommand(
                    tenantId, task1.BackingSolicitationId, task1.Id, instructions[^1].Id,
                    workerAlias, "chief", "bruna", $"attempt:{attemptId}", task1.Version,
                    $"test-assign:{attemptId}", now),
                cts.Token);
            Assert.Equal(WorkChainMutationStatus.Applied, assigned.Status);
            var started = await chain.StartAttemptAsync(
                new WorkAttemptStartCommand(
                    tenantId, task1.BackingSolicitationId, task1.Id, instructions[^1].Id, attemptId,
                    workerAlias, assigned.TaskVersion!.Value, $"test-heartbeat:{attemptId}", now),
                cts.Token);
            Assert.Equal(WorkChainMutationStatus.Applied, started.Status);

            var branch = $"task/agent-run-{attemptId.ToLowerInvariant()}";
            RunGit(repo, "checkout", "-b", branch);
            await File.WriteAllTextAsync(Path.Combine(repo, "agenda.py"), "print('agenda')\n", cts.Token);
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "feat: utilitario de agenda");
            RunGit(repo, "checkout", "main");

            var workspaces = app.Services.GetRequiredService<IAttemptWorkspaceStore>();
            var acquired = await workspaces.AcquireAsync(
                new AttemptWorkspaceAcquireCommand
                {
                    TenantId = tenantId,
                    ProjectId = projectId,
                    TaskId = task1.Id,
                    AttemptId = attemptId,
                    RepositoryRoot = Path.GetFullPath(repo),
                    ControlledRoot = Path.GetFullPath(controlledRoot),
                    BaseReference = "HEAD",
                    BranchName = branch,
                    WorktreePath = Path.Combine(controlledRoot, "worktrees", attemptId),
                    ScopeClaims = ["src/**"],
                    Owner = "test",
                    LeaseDuration = TimeSpan.FromMinutes(5),
                    IdempotencyKey = $"test-workspace:{attemptId}",
                    OccurredAt = DateTimeOffset.UtcNow,
                },
                cts.Token);
            Assert.True(acquired.Succeeded);
            var baseCommit = RunGit(repo, "rev-parse", "HEAD").Trim();
            foreach (var state in new[]
            {
                (From: AttemptWorkspaceState.Claimed, To: AttemptWorkspaceState.Prepared),
                (From: AttemptWorkspaceState.Prepared, To: AttemptWorkspaceState.Running),
                (From: AttemptWorkspaceState.Running, To: AttemptWorkspaceState.Completed),
            })
            {
                var transition = await workspaces.TransitionAsync(
                    new AttemptWorkspaceTransitionCommand
                    {
                        TenantId = tenantId,
                        AttemptId = attemptId,
                        Owner = "test",
                        FencingToken = acquired.Workspace!.FencingToken,
                        ExpectedState = state.From,
                        State = state.To,
                        CommitSha = state.To == AttemptWorkspaceState.Prepared ? baseCommit : null,
                        SessionId = state.To == AttemptWorkspaceState.Running ? "test-session" : null,
                        OccurredAt = DateTimeOffset.UtcNow,
                    },
                    cts.Token);
                Assert.True(transition.Succeeded, $"transição {state.From}->{state.To}: {transition.Status}");
            }

            // 4. COLHEITA: o run Completed vira `awaiting_review` durável e o card vai a `review`.
            var harvested = await service.HarvestCompletedRunsAsync(
                tenantId, project, Path.GetFullPath(controlledRoot), board, chain, cts.Token);
            Assert.Equal(1, harvested);
            var afterHarvest = (await board.GetTaskAsync(tenantId, wave1.Id, cts.Token))!;
            Assert.Equal("review", afterHarvest.State);
            Assert.Equal("awaiting_review", afterHarvest.InternalState);

            // 5a. Veredito de INFRAESTRUTURA nunca é aplicado — o trabalho não é punido pela
            //     falha do crítico; o estado permanece aguardando review.
            var infrastructure = new CriticReviewResult(
                UlidValue.New(DateTimeOffset.UtcNow).ToString(), attemptId, "test-critic", "codex",
                workerAlias, CriticVerdict.Fail, "critic.execution_failed", [], null, null, 0);
            Assert.False(await service.ApplyReviewVerdictAsync(
                tenantId, afterHarvest, attemptId, infrastructure, chain, cts.Token));
            Assert.Equal(
                "awaiting_review",
                (await board.GetTaskAsync(tenantId, wave1.Id, cts.Token))!.InternalState);

            // 5b. Veredito REAL (pass) por conta DIFERENTE do ator é aplicado: `approved`.
            var pass = new CriticReviewResult(
                UlidValue.New(DateTimeOffset.UtcNow).ToString(), attemptId, "test-critic", "codex",
                workerAlias, CriticVerdict.Pass, "critic.pass", [],
                "Implementação coerente com os critérios.", null, 0);
            Assert.True(await service.ApplyReviewVerdictAsync(
                tenantId, afterHarvest, attemptId, pass, chain, cts.Token));
            Assert.Equal(
                "approved",
                (await board.GetTaskAsync(tenantId, wave1.Id, cts.Token))!.InternalState);

            // 6. GATE HUMANO: o merge endpoint integra a branch da tentativa com merge git REAL
            //    e conclui o card (`done`/`completed`) — o trabalho está na base publicada.
            using (var merge = await client.PostAsJsonAsync(
                $"/api/v1/tasks/{wave1.Id}/merge", new { }, cts.Token))
            {
                var payload = await merge.Content.ReadAsStringAsync(cts.Token);
                Assert.True(HttpStatusCode.OK == merge.StatusCode, $"merge devolveu {merge.StatusCode}: {payload}");
                using var body = JsonDocument.Parse(payload);
                Assert.Equal("done", body.RootElement.GetProperty("boardState").GetString());
                Assert.Equal("completed", body.RootElement.GetProperty("internalState").GetString());
            }

            Assert.True(File.Exists(Path.Combine(repo, "agenda.py")),
                "o merge real deve materializar o trabalho da branch na base publicada");
            Assert.Contains("tentativa", RunGit(repo, "log", "-1", "--pretty=%s"), StringComparison.Ordinal);

            // 7. Com T01 entregue, a triagem por ondas promove T02 — o chefe "puxa o próximo".
            var secondWave = await service.PromotePlannedCardsAsync(
                tenantId, project, board, plans, cts.Token);
            Assert.Equal(1, secondWave);
            Assert.Equal("ready", (await board.GetTaskAsync(tenantId, wave2.Id, cts.Token))!.State);
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

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("user.name=Teste");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("user.email=teste@poseidon.local");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} falhou: {stderr}");
        return stdout;
    }
}
