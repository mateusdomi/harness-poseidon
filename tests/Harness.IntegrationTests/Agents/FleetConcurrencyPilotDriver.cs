using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// DRIVER do Piloto 2 — fleet CONCORRENTE real (Parte 1: múltiplas CONTAS distintas).
///
/// NÃO é gate: só roda com <c>HARNESS_RUN_FLEET_PILOT=true</c>. Opera sobre o repositório e
/// os perfis reais (<c>~/.harness/accounts</c>). O chefe (orquestrador) despacha três actors
/// distintos SIMULTANEAMENTE em subárvores DISJUNTAS — backend GLM, backend Claude e
/// frontend Codex — prova worktrees/branches/attempts/claims/PIDs distintos e execução
/// concorrente, roda o critic Antigravity LIVE numa das tarefas, e exercita restart/recovery
/// sem duplicação. NÃO publica: diffs e vereditos ficam em disco para inspeção.
/// </summary>
public sealed class FleetConcurrencyPilotDriver
{
    private static string RepoRoot => Environment.GetEnvironmentVariable("HARNESS_PILOT_REPO")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "harness-poseidon-backend");

    private static string ControlledRoot => Path.GetFullPath(Path.Combine(RepoRoot, ".."));

    private static string ProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness", "accounts");

    private static string ArchiveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness", "pilots");

    private static string AccountsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness", "agent-accounts.json");

    private static string ResultsDir => Environment.GetEnvironmentVariable("HARNESS_PILOT_OUT")
        ?? Path.Combine(Path.GetTempPath(), "harness-fleet-pilot");

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private sealed record FleetSpec(string Account, string Role, string ScopeBase, string RelativeFile);

    private static readonly FleetSpec[] Specs =
    [
        new("worker-glm-general", "backend-specialist",
            "docs/backend/execution/evidence/fleet-glm", "docs/backend/execution/evidence/fleet-glm/probe.md"),
        new("worker-claude-secondary", "backend-specialist",
            "docs/backend/execution/evidence/fleet-claude", "docs/backend/execution/evidence/fleet-claude/probe.md"),
        new("worker-codex-frontend", "frontend-specialist",
            "frontend/fleet-codex", "frontend/fleet-codex/probe.txt"),
    ];

    [Fact]
    public async Task RunConcurrentFleetAcrossDistinctAccounts()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HARNESS_RUN_FLEET_PILOT"), "true", StringComparison.Ordinal))
        {
            return; // Só roda sob solicitação explícita.
        }

        Directory.CreateDirectory(ResultsDir);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var dbPath = Path.Combine(ResultsDir, "fleet.db");

        // GLM autentica por TOKEN de ambiente (o `./poseidon start` carrega `~/.harness/glm.env`;
        // o `dotnet test` não). Resolve o token do Keychain para o ambiente do processo — o
        // provisionador só o repassa ao worker GLM (allowlist), nunca às contas Claude reais.
        await LoadGlmTokenIntoEnvironmentAsync(timeout.Token);

        // UM app conduz do dispatch até o review — parar o app mataria as execuções in-process.
        await using var app = BuildApp(dbPath);
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services), Timeout = TimeSpan.FromMinutes(10) };

        var projectId = await SeedProjectAsync(client, timeout.Token);
        Log($"project={projectId} repo={RepoRoot}");

        var seeded = new List<(FleetSpec Spec, string TaskId)>();
        foreach (var spec in Specs)
        {
            seeded.Add((spec, await SeedTaskAsync(client, projectId, spec, timeout.Token)));
        }

        // DISPARO CONCORRENTE dos três actors distintos.
        var starts = await Task.WhenAll(
            seeded.Select(entry => StartRunAsync(client, projectId, entry.Spec, entry.TaskId, timeout.Token)));

        // Prova de ISOLAMENTO: attempts, branches e worktrees todos distintos.
        Assert.Equal(3, starts.Select(s => s.AttemptId).Distinct().Count());
        Assert.Equal(3, starts.Select(s => s.Branch).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, starts.Select(s => s.Worktree).Distinct(StringComparer.Ordinal).Count());
        Log($"3 attempts distintos, worktrees disjuntas: {string.Join(" | ", starts.Select(s => s.Worktree))}");

        var maxConcurrent = await PollConcurrencyAsync(client, starts.Select(s => s.AttemptId).ToArray(), timeout.Token);
        Log($"máximo de execuções simultâneas observadas: {maxConcurrent}");

        var orchestrator = app.Services.GetRequiredService<AgentRunOrchestrator>();
        var finals = new List<(FleetSpec Spec, string AttemptId, string Status, string? Error, string? Message)>();
        foreach (var start in starts)
        {
            var completion = orchestrator.WaitAsync(start.AttemptId);
            var result = completion is null ? null : await completion.WaitAsync(timeout.Token);
            var status = result?.Status.ToString() ?? "unknown";
            finals.Add((start.Spec, start.AttemptId, status, result?.FinalError, result?.Execution?.FinalMessage));
            Log($"[{start.Spec.Account}] terminal={status} error={result?.FinalError} msg={Trunc(result?.Execution?.FinalMessage, 200)}");
        }

        // Critic Antigravity LIVE numa das tarefas concluídas (actor≠critic).
        var target = finals.FirstOrDefault(f => f.Status == "Completed");
        string reviewBody = "(nenhuma tarefa Completed para revisar)";
        if (target.AttemptId is not null)
        {
            var targetStart = starts.First(s => s.AttemptId == target.AttemptId);
            var diff = await CaptureDiffAsync(targetStart.Worktree, targetStart.Branch, timeout.Token);
            using var review = await client.PostAsJsonAsync(
                $"/api/v1/agent-runs/{target.AttemptId}/review",
                new
                {
                    critic = "worker-antigravity-review",
                    actor = target.Spec.Account,
                    diff = string.IsNullOrWhiteSpace(diff) ? "(diff vazio)" : diff,
                    reviewDirectory = ControlledRoot,
                    acceptanceCriteria = new[] { $"O arquivo {target.Spec.RelativeFile} foi criado com uma linha." },
                    scopeClaims = new[] { $"{target.Spec.ScopeBase}/**" },
                },
                timeout.Token);
            reviewBody = await review.Content.ReadAsStringAsync(timeout.Token);
            Log($"critic antigravity status={(int)review.StatusCode} verdict:\n{reviewBody}");
        }

        await app.StopAsync(timeout.Token);

        // RESTART/RECOVERY: reabre no mesmo banco e reconcilia — sem duplicar attempt/worker.
        await using var recovered = BuildApp(dbPath);
        await recovered.StartAsync(timeout.Token);
        using var recoverClient = new HttpClient { BaseAddress = BaseAddress(recovered.Services), Timeout = TimeSpan.FromMinutes(2) };
        await EnsureSessionAsync(recoverClient, timeout.Token);
        using var recover = await recoverClient.PostAsync("/api/v1/agent-runs/recovery", content: null, timeout.Token);
        var recoverBody = await recover.Content.ReadAsStringAsync(timeout.Token);
        Log($"recovery status={(int)recover.StatusCode} body={recoverBody}");
        await recovered.StopAsync(timeout.Token);

        // Relatório final.
        var report = new
        {
            projectId,
            maxConcurrent,
            runs = finals.Select(f => new { f.Spec.Account, f.Spec.Role, f.AttemptId, f.Status, f.Error }),
            antigravityReview = reviewBody,
            recovery = recoverBody,
        };
        await File.WriteAllTextAsync(
            Path.Combine(ResultsDir, "fleet-report.json"),
            JsonSerializer.Serialize(report, ReportJson),
            timeout.Token);
        Log("== fleet pilot finished (no publish) ==");

        Assert.True(maxConcurrent >= 2, "Esperava ao menos 2 execuções simultâneas.");
    }

    private sealed record StartedRun(FleetSpec Spec, string AttemptId, string Branch, string Worktree);

    private static async Task<StartedRun> StartRunAsync(
        HttpClient client, string projectId, FleetSpec spec, string taskId, CancellationToken token)
    {
        var instruction =
            $"Você está numa worktree Git isolada. Crie EXATAMENTE o arquivo `{spec.RelativeFile}` " +
            $"contendo uma única linha de texto: `fleet-ok {spec.Account}`. Crie os diretórios necessários. " +
            "Não altere nenhum outro arquivo, não rode comandos além do necessário e finalize.";

        using var start = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId,
                role = spec.Role,
                account = spec.Account,
                instruction,
                scopeClaims = new[] { $"{spec.ScopeBase}/**" },
            },
            token);
        var body = await start.Content.ReadAsStringAsync(token);
        Log($"[{spec.Account}] start status={(int)start.StatusCode} body={Trunc(body, 400)}");
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        using var doc = JsonDocument.Parse(body);
        return new StartedRun(
            spec,
            doc.RootElement.GetProperty("attemptId").GetString()!,
            doc.RootElement.GetProperty("branchName").GetString()!,
            doc.RootElement.GetProperty("worktreePath").GetString()!);
    }

    private static async Task<int> PollConcurrencyAsync(
        HttpClient client, string[] attemptIds, CancellationToken token)
    {
        var max = 0;
        for (var i = 0; i < 120; i++)
        {
            var running = 0;
            var terminal = 0;
            foreach (var id in attemptIds)
            {
                using var response = await client.GetAsync($"/api/v1/agent-runs/{id}", token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var status = doc.RootElement.GetProperty("status").GetString();
                if (status is "running" or "accepted")
                {
                    running++;
                }
                else if (status is "completed" or "failed" or "cancelled" or "rejected")
                {
                    terminal++;
                }
            }

            max = Math.Max(max, running);
            if (terminal == attemptIds.Length)
            {
                break;
            }

            await Task.Delay(1000, token);
        }

        return max;
    }

    private static async Task<string> SeedTaskAsync(
        HttpClient client, string projectId, FleetSpec spec, CancellationToken token)
    {
        var label = spec.Account;
        using var solicitation = await client.PostAsJsonAsync(
            "/api/v1/solicitations",
            new CreateSolicitationRequest(projectId, "request", $"Fleet {label}", $"Probe de fleet para {label}."),
            token);
        solicitation.EnsureSuccessStatusCode();
        var solicitationId = Id(await solicitation.Content.ReadAsStringAsync(token));

        using var demand = await client.PostAsJsonAsync(
            "/api/v1/demands",
            new CreateDemandRequest(projectId, $"Fleet {label}", $"Criar o probe de {label}.", solicitationId),
            token);
        demand.EnsureSuccessStatusCode();
        var demandId = Id(await demand.Content.ReadAsStringAsync(token));

        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, $"Fleet {label}", $"Criar {spec.RelativeFile}.", demandId),
            token);
        task.EnsureSuccessStatusCode();
        return Id(await task.Content.ReadAsStringAsync(token));
    }

    private static async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Operador", null, null, "pt-BR"), token);
        profile.EnsureSuccessStatusCode();

        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        organization.EnsureSuccessStatusCode();
        var organizationId = Id(await organization.Content.ReadAsStringAsync(token));

        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Fleet concorrente do Piloto 2.",
                RepositoryUrl = Path.GetFullPath(RepoRoot),
            },
            token);
        project.EnsureSuccessStatusCode();
        return Id(await project.Content.ReadAsStringAsync(token));
    }

    private static async Task EnsureSessionAsync(HttpClient client, CancellationToken token)
    {
        // No app de recovery o perfil já existe no mesmo banco: o middleware adota o perfil
        // local numa requisição sem cookie, então basta tocar um endpoint autenticado.
        using var response = await client.GetAsync("/api/v1/profiles/current", token);
        _ = response;
    }

    private static async Task LoadGlmTokenIntoEnvironmentAsync(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")))
        {
            return;
        }

        var user = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
        var result = await RunAsync(
            "security", ["find-generic-password", "-s", "poseidon-glm-general", "-a", user, "-w"],
            Path.GetTempPath(), token);
        var value = result.StdOut.Trim();
        if (result.ExitCode == 0 && value.Length > 0)
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://api.z.ai/api/anthropic");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", value);
            Environment.SetEnvironmentVariable("ANTHROPIC_DEFAULT_SONNET_MODEL", "glm-5.2");
            Environment.SetEnvironmentVariable("ANTHROPIC_DEFAULT_OPUS_MODEL", "glm-5.2");
            Log("GLM token resolvido do Keychain para o ambiente do driver.");
        }
        else
        {
            Log("GLM token AUSENTE no Keychain (poseidon-glm-general) — o worker GLM não autenticará.");
        }
    }

    private static async Task<string> CaptureDiffAsync(string worktree, string branch, CancellationToken token)
    {
        // O worker escreve na worktree e o orquestrador PRESERVA a worktree suja (trabalho
        // não commitado). O diff real está no working tree, não nos commits da branch.
        if (Directory.Exists(worktree))
        {
            await RunAsync("git", ["-C", worktree, "add", "-A"], worktree, token);
            var dirty = await RunAsync("git", ["-C", worktree, "diff", "--cached", "origin/develop"], worktree, token);
            if (!string.IsNullOrWhiteSpace(dirty.StdOut))
            {
                return dirty.StdOut;
            }
        }

        var committed = await RunAsync("git", ["-C", RepoRoot, "diff", $"origin/develop...{branch}"], RepoRoot, token);
        return committed.StdOut;
    }

    private static WebApplication BuildApp(string dbPath) => HostApplication.Build([
        "--urls", "http://127.0.0.1:0",
        "--Harness:DatabasePath", dbPath,
        "--Harness:AgentRuns:Enabled", "true",
        "--Harness:AgentRuns:ControlledRoot", ControlledRoot,
        "--Harness:AgentRuns:ProfilesRoot", ProfilesRoot,
        "--Harness:AgentRuns:AccountsFilePath", AccountsFile,
        "--Harness:AgentRuns:ArchiveRoot", ArchiveRoot,
        "--Harness:AgentRuns:RunTimeout", "00:20:00",
        "--Harness:AgentRuns:LeaseDuration", "00:20:00",
    ]);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string file, string[] args, string cwd, CancellationToken token)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string Id(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static string Trunc(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";

    private static Uri BaseAddress(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item => item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    private static void Log(string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}";
        Console.Error.WriteLine(line);
        File.AppendAllText(Path.Combine(ResultsDir, "fleet-driver.log"), line + Environment.NewLine);
    }
}
