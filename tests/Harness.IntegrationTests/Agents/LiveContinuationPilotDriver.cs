using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// DRIVER da execução REAL do Piloto 1B — continuação governada com o Codex autenticado.
///
/// NÃO é um gate: só roda com <c>HARNESS_RUN_LIVE_PILOT=true</c>. Opera sobre o repositório
/// real, os perfis reais (<c>~/.harness/accounts</c>) e o arquivo real
/// (<c>~/.harness/pilots</c>). Reusa a mesma máquina durável do produto (nada é stubado): o
/// executor externo é o Codex de verdade, e falha só se a conta não estiver autenticada.
///
/// Fluxo: semeia projeto/tarefa/attempt-1 reprovado → arquiva o patch real do Piloto 1A →
/// dispara a continuação (`resumeFromAttemptId`) → aguarda o worker real → captura o diff →
/// roda o critic real. NÃO publica: o veredito e o diff são escritos em disco para inspeção
/// antes de qualquer integração em `develop`.
/// </summary>
public sealed class LiveContinuationPilotDriver
{
    private static string RepoRoot => Environment.GetEnvironmentVariable("HARNESS_PILOT_REPO")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "harness-poseidon-backend");

    private static string ControlledRoot => Path.GetFullPath(Path.Combine(RepoRoot, ".."));

    private static string ResultsDir => Environment.GetEnvironmentVariable("HARNESS_PILOT_OUT")
        ?? Path.Combine(Path.GetTempPath(), "harness-live-pilot");

    private static string ArchiveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness", "pilots");

    private static string WorkerInstruction =>
        Environment.GetEnvironmentVariable("HARNESS_PILOT_INSTRUCTION_FILE") is { Length: > 0 } file
            && File.Exists(file)
            ? File.ReadAllText(file)
            : DefaultWorkerInstruction;

    private static string ResumeAttemptId =>
        Environment.GetEnvironmentVariable("HARNESS_PILOT_RESUME_ATTEMPT") is { Length: > 0 } id
            ? id
            : NewId();

    private static ArchivedAttemptFinding[] ConfiguredFindings()
    {
        var raw = Environment.GetEnvironmentVariable("HARNESS_PILOT_FINDINGS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultFindings;
        }

        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 3))
            .Where(parts => parts.Length == 3)
            .Select(parts => new ArchivedAttemptFinding(parts[0], parts[1], parts[2]))
            .ToArray();
    }

    private static readonly ArchivedAttemptFinding[] DefaultFindings =
    [
        new ArchivedAttemptFinding("P0", "suite-vermelha", "1 teste falhando; suíte não verde"),
        new ArchivedAttemptFinding("P1", "estado-inicial-desonesto", "UI exibe Concluído antes de existir turno"),
        new ArchivedAttemptFinding("P1", "migracao-enum-turno-incompleta", "troca de chiefTurnState não fechada"),
        new ArchivedAttemptFinding("P1", "codigos-crus-na-ui", "blocker.code/action.code sem i18n"),
    ];

    private const string DefaultWorkerInstruction = """
        Você é o worker-codex-frontend continuando a tentativa reprovada do Piloto 1A. O diff
        anterior foi APLICADO nesta worktree como ponto de partida (ou, se estiver stale,
        segue no prompt como CONTEXTO — nesse caso reconstrua sobre a base atual). Sua tarefa
        é FECHAR TODOS os nove achados do critic sem reintroduzir nenhum. Escopo autorizado:
        SOMENTE frontend/** e docs/frontend/**.

        P0 — suite-vermelha: a suíte do frontend precisa ficar VERDE em `npm run check`. Hoje
        há testes de drift vermelhos (events-drift, contracts, governance-openapi-drift)
        porque `agentRun.stateChanged` e os eventos do lifecycle C2/C3 não têm schema no
        catálogo do frontend. Adicione os schemas de payload em src/api/contracts/events.ts
        cobrindo EXATAMENTE docs/contracts/events.json v1.2, incluindo agentRun.stateChanged.

        P1 — estado-inicial-desonesto: o estado inicial NUNCA pode ser `completed`. A UI não
        pode exibir sucesso/"Concluído" antes de existir qualquer turno. Ausência de turno =
        idle/notStarted/unconfigured (ou equivalente).
        P1 — migracao-enum-turno-incompleta: complete e TESTE a migração de `chiefTurnState`
        (pending/processing/completed/failed).
        P1 — codigos-crus-na-ui: `blocker.code` e `nextAction.code` passam por catálogo i18n
        (en.json/pt-BR.json). Código cru só em disclosure técnico.

        P2 — readiness-do-handle-nao-consumido: consuma/justifique o `readiness` do schema.
        P2 — e2e-sem-evidencia: execute os specs Playwright alterados e produza evidência.
        P2 — catalogo-canonico-nao-verificavel: torne o SHA/catálogo verificável.
        P2 — banner-bloqueado-persistente: limpe o aviso de bloqueio ao trocar de conversa.

        P3 — disabled-hardcoded: remova `disabled={false}` e ruído equivalente.

        Rode os gates aplicáveis (npm run check, build, build-storybook, e2e, a11y) na worktree
        e garanta zero console error e zero asset 404. Ao terminar, faça UM commit com TODAS
        as mudanças: `git add -A && git commit -m "feat(frontend): close the nine critic
        findings"`. NÃO altere nada fora de frontend/** e docs/frontend/**.
        """;

    [Fact]
    public async Task RunLiveGovernedContinuation()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HARNESS_RUN_LIVE_PILOT"), "true", StringComparison.Ordinal))
        {
            return; // Não é gate: só roda sob solicitação explícita.
        }

        Directory.CreateDirectory(ResultsDir);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));

        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(ResultsDir, "pilot.db"),
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:AgentRuns:ControlledRoot", ControlledRoot,
            "--Harness:AgentRuns:ArchiveRoot", ArchiveRoot,
            "--Harness:AgentRuns:RunTimeout", "00:30:00",
            "--Harness:AgentRuns:LeaseDuration", "00:30:00",
        ]);
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services), Timeout = TimeSpan.FromMinutes(40) };

        Log("== Live continuation pilot ==");
        var projectId = await SeedProjectAsync(client, timeout.Token);
        var (taskId, priorAttemptId) = await SeedRejectedAttemptAsync(app, client, projectId, timeout.Token);
        Log($"project={projectId} task={taskId} priorAttempt={priorAttemptId}");

        var patch = await File.ReadAllTextAsync(ArchivedPilotPatchPath(), timeout.Token);
        var manifest = new AttemptArtifactArchive(ArchiveRoot).Write(
            RejectedManifest(priorAttemptId, projectId, taskId), patch);
        Log($"archived patch sha256={manifest.PatchSha256}");

        using var start = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId,
                resumeFromAttemptId = priorAttemptId,
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = WorkerInstruction,
            },
            timeout.Token);
        var startBody = await start.Content.ReadAsStringAsync(timeout.Token);
        Log($"start status={(int)start.StatusCode} body={startBody}");
        Assert.Equal(System.Net.HttpStatusCode.Accepted, start.StatusCode);
        using var startDoc = JsonDocument.Parse(startBody);
        var newAttemptId = startDoc.RootElement.GetProperty("attemptId").GetString()!;
        var branch = startDoc.RootElement.GetProperty("branchName").GetString()!;
        var worktree = startDoc.RootElement.GetProperty("worktreePath").GetString()!;
        Assert.NotEqual(priorAttemptId, newAttemptId);
        Log($"new attempt={newAttemptId} branch={branch} worktree={worktree}");

        var orchestrator = app.Services.GetRequiredService<AgentRunOrchestrator>();
        var completion = orchestrator.WaitAsync(newAttemptId);
        Assert.NotNull(completion);
        var final = await completion!.WaitAsync(timeout.Token);
        Log($"worker terminal status={final.Status} session={final.SessionId} error={final.FinalError}");
        Log($"worker final message:\n{final.Execution?.FinalMessage}");

        // Captura o diff completo da continuação (patch anterior + correções do worker),
        // preferindo o commit na branch; se ficou sujo, o diff da worktree.
        var diff = await CaptureDiffAsync(worktree, branch, timeout.Token);
        var diffPath = Path.Combine(ResultsDir, "continuation.diff");
        await File.WriteAllTextAsync(diffPath, diff, timeout.Token);
        Log($"captured diff bytes={diff.Length} -> {diffPath}");

        // Bateria de gates do frontend executada pelo OPERADOR na worktree, incluindo o
        // test:e2e:real contra um Host real — a prova que o critic exige. O worker não
        // consegue subir o Host .NET no próprio sandbox; o operador sim.
        var gateEvidence = await RunFrontendGatesAsync(worktree, timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(ResultsDir, "frontend-gates.log"), gateEvidence, timeout.Token);
        Log($"frontend gates evidence bytes={gateEvidence.Length}");

        // Critic real e independente (conta diferente do actor).
        using var review = await client.PostAsJsonAsync(
            $"/api/v1/agent-runs/{newAttemptId}/review",
            new
            {
                critic = "chief-claude-primary",
                actor = "worker-codex-frontend",
                diff,
                reviewDirectory = worktree,
                testEvidence = Tail(gateEvidence, 12000),
                scopeClaims = FrontendScopeClaims,
                acceptanceCriteria = manifest.Findings
                    .Select(f => $"[{f.Severity} {f.Code}] {f.Summary}").ToArray(),
            },
            timeout.Token);
        var reviewBody = await review.Content.ReadAsStringAsync(timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(ResultsDir, "verdict.json"), reviewBody, timeout.Token);
        Log($"critic status={(int)review.StatusCode} verdict:\n{reviewBody}");

        await app.StopAsync(timeout.Token);
        Log("== pilot driver finished (no publish) ==");
    }

    private static string ArchivedPilotPatchPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("HARNESS_PILOT_PATCH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        return Directory.EnumerateFiles(ArchiveRoot, "*.patch")
            .OrderBy(path => path, StringComparer.Ordinal)
            .First(path => !Path.GetFileName(path).Contains(".manifest", StringComparison.Ordinal));
    }

    private static readonly string[] FrontendScopeClaims = ["frontend/**", "docs/frontend/**"];
    private static readonly string[] NpmCiArgs = ["ci", "--no-audit", "--loglevel=error"];
    private static readonly string[] NpmCheckArgs = ["run", "check"];

    private static ArchivedAttemptManifest RejectedManifest(string attemptId, string projectId, string taskId) => new()
    {
        AttemptId = attemptId,
        TenantId = "01KY000000000000000000TEN0",
        ProjectId = projectId,
        TaskId = taskId,
        Role = "frontend-specialist",
        ActorAlias = "worker-codex-frontend",
        ControlledRepositoryRoot = Path.GetFullPath(RepoRoot),
        SourceCommit = "6e4f71378bdd231ecc309e68ca82c2f55abde1e8",
        PatchFileName = $"{attemptId}.patch",
        PatchSha256 = "sealed-on-write",
        Verdict = "fail",
        ReviewId = "01KY37PQX9921N4SHPE620CJ0Q",
        ReceiptTurnId = null,
        ScopeClaims = ["frontend/**", "docs/frontend/**"],
        Findings = ConfiguredFindings(),
        ArchivedAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task<(string TaskId, string AttemptId)> SeedRejectedAttemptAsync(
        WebApplication app, HttpClient client, string projectId, CancellationToken token)
    {
        using var solicitation = await client.PostAsJsonAsync(
            "/api/v1/solicitations",
            new CreateSolicitationRequest(projectId, "request", "Integração C2/C3 no frontend", "Continuar Piloto 1A."),
            token);
        solicitation.EnsureSuccessStatusCode();
        var solicitationId = (await JsonDocument.ParseAsync(await solicitation.Content.ReadAsStreamAsync(token), cancellationToken: token))
            .RootElement.GetProperty("id").GetString()!;

        using var demand = await client.PostAsJsonAsync(
            "/api/v1/demands",
            new CreateDemandRequest(projectId, "Integração C2/C3", "Fechar achados do critic.", solicitationId),
            token);
        demand.EnsureSuccessStatusCode();
        var demandId = (await JsonDocument.ParseAsync(await demand.Content.ReadAsStreamAsync(token), cancellationToken: token))
            .RootElement.GetProperty("id").GetString()!;

        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, "Integração C2/C3", "Integrar o turno bloqueado e o lifecycle.", demandId),
            token);
        task.EnsureSuccessStatusCode();
        var taskId = (await JsonDocument.ParseAsync(await task.Content.ReadAsStreamAsync(token), cancellationToken: token))
            .RootElement.GetProperty("id").GetString()!;

        var board = app.Services.GetRequiredService<IWorkBoardStore>();
        var chain = app.Services.GetRequiredService<IWorkChainStore>();
        var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>().ListAsync(token))[0].TenantId;
        var persisted = (await board.GetTaskAsync(tenantId, taskId, token))!;
        var backing = persisted.BackingSolicitationId;
        var instruction = (await board.ListInstructionsAsync(tenantId, taskId, null, 10, token))[0];
        var attemptId = ResumeAttemptId;
        var now = DateTimeOffset.UtcNow;

        var started = await chain.StartAttemptAsync(new WorkAttemptStartCommand(
            tenantId, backing, taskId, instruction.Id, attemptId, "worker-codex-frontend",
            persisted.Version, $"pilot:{attemptId}", now), token);
        Assert.Equal(WorkChainMutationStatus.Applied, started.Status);

        var completed = await chain.CompleteAttemptAsync(new WorkAttemptCompleteCommand(
            tenantId, backing, taskId, attemptId, started.TaskVersion!.Value,
            [new WorkEvidenceInput(NewId(), "workspace:task/prior@6e4f713")],
            $"pilot:complete:{attemptId}", now.AddSeconds(1)), token);
        Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);

        var rejected = await chain.ReviewAttemptAsync(new WorkAttemptReviewCommand(
            tenantId, backing, taskId, attemptId, NewId(), "chief-claude-primary",
            "rejected", "Nove achados P0-P3; suíte vermelha.", completed.TaskVersion!.Value,
            $"pilot:review:{attemptId}", now.AddSeconds(2)), token);
        Assert.Equal(WorkChainMutationStatus.Applied, rejected.Status);

        const string correction = "Feche os nove achados do critic sobre o diff anterior.";
        var v2 = await chain.AddInstructionVersionAsync(new WorkInstructionVersionCreateCommand(
            tenantId, backing, taskId, NewId(), correction,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(correction))),
            rejected.TaskVersion!.Value, $"pilot:instr2:{attemptId}", now.AddSeconds(3)), token);
        Assert.Equal(WorkChainMutationStatus.Applied, v2.Status);

        return (taskId, attemptId);
    }

    private static async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Operador", null, null, "pt-BR"), token);
        profile.EnsureSuccessStatusCode();

        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        organization.EnsureSuccessStatusCode();
        var organizationId = (await JsonDocument.ParseAsync(await organization.Content.ReadAsStreamAsync(token), cancellationToken: token))
            .RootElement.GetProperty("id").GetString()!;

        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Continuação governada do Piloto 1A.",
                RepositoryUrl = Path.GetFullPath(RepoRoot),
            },
            token);
        project.EnsureSuccessStatusCode();
        return (await JsonDocument.ParseAsync(await project.Content.ReadAsStreamAsync(token), cancellationToken: token))
            .RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CaptureDiffAsync(string worktree, string branch, CancellationToken token)
    {
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

    private static readonly string[] NpmE2eArgs = ["run", "test:e2e"];
    private static readonly string[] NpmA11yArgs = ["run", "test:a11y"];
    // --workers=1: os projetos de viewport compartilham UM Host efêmero; rodar em paralelo
    // fazia os projetos posteriores esgotarem o timeout de sessão. Serial elimina a
    // contenção sem reduzir a cobertura.
    private static readonly string[] NpmE2eRealArgs = ["run", "test:e2e:real", "--", "--workers=1"];

    private static async Task<string> RunFrontendGatesAsync(string worktree, CancellationToken token)
    {
        var frontend = Path.Combine(worktree, "frontend");
        if (!Directory.Exists(frontend))
        {
            return "frontend directory absent in worktree";
        }

        var log = new StringBuilder();
        void Section(string label, int exit, string body, int cap) => log
            .Append("$ ").Append(label).Append(" -> exit ")
            .Append(exit.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append('\n').Append(Tail(body, cap)).Append("\n\n");

        var ci = await RunAsync("npm", NpmCiArgs, frontend, token);
        Section("npm ci", ci.ExitCode, ci.StdOut + ci.StdErr, 1500);

        var check = await RunAsync("npm", NpmCheckArgs, frontend, token);
        Section("npm run check", check.ExitCode, check.StdOut + check.StdErr, 4000);

        var e2e = await RunAsync("npm", NpmE2eArgs, frontend, token, ci: true);
        Section("npm run test:e2e (mock)", e2e.ExitCode, e2e.StdOut + e2e.StdErr, 4000);

        var a11y = await RunAsync("npm", NpmA11yArgs, frontend, token, ci: true);
        Section("npm run test:a11y", a11y.ExitCode, a11y.StdOut + a11y.StdErr, 2500);

        // test:e2e:real precisa de um Host real; o operador sobe um subprocesso efêmero.
        const int backendPort = 5091;
        var (host, hostDb) = StartEphemeralHost(backendPort);
        try
        {
            await WaitForHostAsync($"http://127.0.0.1:{backendPort}/openapi/v1.json", token);
            var real = await RunAsync(
                "npm", NpmE2eRealArgs, frontend, token, ci: true,
                extraEnv: ("POSEIDON_BACKEND_URL", $"http://127.0.0.1:{backendPort}"));
            Section("npm run test:e2e:real", real.ExitCode, real.StdOut + real.StdErr, 6000);
        }
        finally
        {
            try { if (!host.HasExited) { host.Kill(entireProcessTree: true); } } catch (InvalidOperationException) { }
            try { Directory.Delete(Path.GetDirectoryName(hostDb)!, recursive: true); } catch (IOException) { }
        }

        var audit = await RunAsync("npm", ["audit", "--omit=dev", "--audit-level=high"], frontend, token);
        Section("npm audit --omit=dev", audit.ExitCode, audit.StdOut, 1500);
        return log.ToString();
    }

    private static (Process Host, string DbPath) StartEphemeralHost(int port)
    {
        var dir = Path.Combine(ResultsDir, $"e2e-host-{port}");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "host.db");
        var dll = Path.Combine(RepoRoot, "src", "Harness.Host", "bin", "Release", "net10.0", "Harness.Host.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { dll, "--urls", $"http://127.0.0.1:{port}", "--Harness:DatabasePath", db })
        {
            psi.ArgumentList.Add(a);
        }

        return (Process.Start(psi)!, db);
    }

    private static async Task WaitForHostAsync(string url, CancellationToken token)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var response = await probe.GetAsync(url, token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            await Task.Delay(1000, token);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string file, string[] args, string cwd, CancellationToken token,
        bool ci = false, (string Key, string Value)? extraEnv = null)
    {
        var psi = new ProcessStartInfo(file)
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

        if (ci)
        {
            psi.Environment["CI"] = "1";
        }

        if (extraEnv is { } env)
        {
            psi.Environment[env.Key] = env.Value;
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string NewId() => SharedKernel.Identifiers.UlidValue.New(DateTimeOffset.UtcNow).ToString();

    private static string Tail(string value, int max) =>
        value.Length <= max ? value : "…\n" + value[^max..];

    private static void Log(string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}";
        Console.Error.WriteLine(line);
        File.AppendAllText(Path.Combine(ResultsDir, "driver.log"), line + Environment.NewLine);
    }

    private static Uri BaseAddress(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item => item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
