using Harness.IntegrationTests.Security;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Host.Tools;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// CA-5 — caminho FELIZ do bootstrap governado sobre um repositório Git real.
///
/// Este teste não depende de credencial externa e prova a máquina durável de ponta a ponta:
/// claim adquirido, branch e worktree reais criadas, context bundle e receipt de governança
/// gravados, transição durável, e cleanup completo — claim liberado, concessão da conta
/// liberada, worktree removida e nenhum processo órfão.
///
/// O executor externo termina em falha de AUTENTICAÇÃO (o perfil isolado da conta não está
/// logado nesta máquina). Isso é o esperado e é o ponto: o run falha no lugar certo, pelo
/// motivo certo, e ainda assim libera tudo o que adquiriu.
/// </summary>
public sealed class AgentRunHappyPathTests : IDisposable
{
    private readonly string _root;

    public AgentRunHappyPathTests()
    {
        var candidate = Path.Combine(
            Path.GetTempPath(), $"harness-run-happy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidate);
        // No macOS o diretório temporário fica sob `/var/folders`, que é symlink para
        // `/private/var`. O Git responde sempre com o caminho REAL, então a raiz do teste
        // precisa ser resolvida — senão a validação de contenção do worktree falha.
        _root = ResolveRealPath(candidate);
    }

    private string ControlledRoot => Path.Combine(_root, "controlled");

    private string RepositoryRoot => Path.Combine(ControlledRoot, "project");

    [Fact]
    public async Task TheBootstrapAcquiresClaimWorktreeBundleAndReceiptThenReleasesEverything()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CreateRepository();

        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(_root, "harness.db"),
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:AgentRuns:ControlledRoot", ControlledRoot,
            // Ledger de disponibilidade PRÓPRIO do teste: sem isto o Host de teste lê e escreve
            // o ledger da instalação real do operador — uma conta em cooldown na máquina
            // reprovava a suíte, e o teste sujava o estado de produção do dono.
            "--Harness:AgentRuns:AvailabilityLedgerPath",
                Path.Combine(Path.GetTempPath(), $"harness-availability-{Guid.NewGuid():N}.json"),
            "--Harness:AgentRuns:ProfilesRoot", Path.Combine(_root, "profiles"),
            "--Harness:AgentRuns:RunTimeout", "00:02:00",
        ]);
        await app.StartAsync(timeout.Token);

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };

        var projectId = await SeedProjectAsync(client, timeout.Token);
        await UnsafeExecutionSetup.AcceptAsync(client, projectId, timeout.Token);
        var (taskId, attemptId) = await SeedTaskAndAttemptAsync(app, client, projectId, timeout.Token);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId,
                attemptId,
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "Adicione um comentário de cabeçalho ao README do frontend.",
            },
            timeout.Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var accepted = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(timeout.Token));
        var run = accepted.RootElement;
        Assert.Equal(attemptId, run.GetProperty("attemptId").GetString());

        // O claim foi adquirido com o escopo do PAPEL — não com um escopo pedido pelo cliente.
        Assert.Equal("accepted", run.GetProperty("status").GetString());
        Assert.Equal("worker-codex-frontend", run.GetProperty("account").GetString());
        Assert.Equal("codex", run.GetProperty("executorId").GetString());
        Assert.Equal(
            ["docs/frontend/**", "frontend/**"],
            run.GetProperty("scopeClaims").EnumerateArray()
                .Select(item => item.GetString()).Order(StringComparer.Ordinal));
        Assert.True(run.GetProperty("workspaceFencingToken").GetInt64() > 0);
        Assert.True(run.GetProperty("accountFencingToken").GetInt64() > 0);

        var branchName = run.GetProperty("branchName").GetString()!;
        var worktreePath = run.GetProperty("worktreePath").GetString()!;

        var orchestrator = app.Services.GetRequiredService<AgentRunOrchestrator>();
        var completion = orchestrator.WaitAsync(attemptId);
        Assert.NotNull(completion);
        var final = await completion.WaitAsync(timeout.Token);

        // A branch de tentativa foi criada de verdade no repositório.
        Assert.True(
            (await GitAsync("branch --list --format=%(refname:short)")).Contains(branchName, StringComparison.Ordinal),
            $"branch ausente. status={final.Status} error={final.FinalError} bundle={final.BundleChecksum}");

        // O receipt de governança existe e o bundle foi selecionado por manifest.
        Assert.False(string.IsNullOrWhiteSpace(final.BundleChecksum));
        using var receipts = JsonDocument.Parse(await client.GetStringAsync(
            "/api/v1/governance-runtime/receipts", timeout.Token));
        var published = receipts.RootElement.ValueKind == JsonValueKind.Array
            ? receipts.RootElement
            : receipts.RootElement.EnumerateObject()
                .First(property => property.Value.ValueKind == JsonValueKind.Array).Value;
        Assert.Contains(
            published.EnumerateArray(),
            item => item.GetProperty("turnId").GetString() == final.RunId);

        // O executor falhou por AUTENTICAÇÃO — o perfil isolado não está logado. É o
        // resultado honesto nesta máquina, e o run não finge sucesso.
        Assert.Equal(AgentRunStatus.Failed, final.Status);

        // Fase 3 — a invocação real vira FATO DURÁVEL: conta, provedor, card, tentativa,
        // duração e desfecho em `model_invocations`. Como este executor não expôs uso, o
        // desfecho carrega `usage_unknown`: zero desconhecido nunca é lido como zero medido.
        var tenantId = await ProfileTenantIdAsync(app);
        var invocations = app.Services.GetRequiredService<IModelInvocationStore>();
        var invocation = Assert.Single(
            await invocations.GetTaskInvocationsAsync(tenantId, taskId, timeout.Token));
        Assert.Equal("worker-codex-frontend", invocation.AccountAlias);
        Assert.Equal(attemptId, invocation.AttemptId);
        Assert.Equal(projectId, invocation.ProjectId);
        Assert.Contains("usage_unknown", invocation.Outcome, StringComparison.Ordinal);
        Assert.Equal(0, invocation.InputTokens);
        Assert.Equal(0m, await invocations.GetTotalCostAsync(tenantId, projectId, timeout.Token));

        // Fase 4 — a execução real passou pelo PEP ANTES de tocar o executor, e a decisão de
        // capability virou entrada no `audit_ledger` append-only: ator, card, tentativa, operação
        // e recurso. Sem esta linha, "só a capability autoriza" seria confiança, não prova.
        var ledger = app.Services.GetRequiredService<IAuditEventStore>();
        var decisions = await ledger.ListAsync(new AuditEventQuery(tenantId, null, 200), timeout.Token);
        var capabilityEntry = Assert.Single(
            decisions, entry => entry.Action == "capability.allowed");
        Assert.Equal("agent", capabilityEntry.ActorKind);
        Assert.Equal(taskId, capabilityEntry.TargetId);
        Assert.Contains("actor=worker-codex-frontend", capabilityEntry.Detail!, StringComparison.Ordinal);
        Assert.Contains("operation=ToolExecution", capabilityEntry.Detail!, StringComparison.Ordinal);
        Assert.Contains("tool=codex", capabilityEntry.Detail!, StringComparison.Ordinal);
        Assert.True((await ledger.VerifyIntegrityAsync(tenantId, timeout.Token)).Valid);

        // Cleanup completo: estado terminal, claim liberado, worktree removida e concessão
        // da conta devolvida. Nada fica preso.
        var workspace = app.Services.GetRequiredService<IAttemptWorkspaceStore>();
        var snapshot = await workspace.GetAsync(tenantId, attemptId, timeout.Token);
        Assert.NotNull(snapshot);
        Assert.Equal(AttemptWorkspaceState.Failed, snapshot.State);
        Assert.NotNull(snapshot.ReleasedAt);
        Assert.False(Directory.Exists(worktreePath));

        using var doctor = JsonDocument.Parse(await client.GetStringAsync(
            "/api/v1/agent-accounts/doctor", timeout.Token));
        var account = doctor.RootElement.GetProperty("accounts").EnumerateArray()
            .Single(item => item.GetProperty("alias").GetString() == "worker-codex-frontend");
        Assert.True(account.GetProperty("profileHealthy").GetBoolean());
        Assert.False(account.GetProperty("authenticated").GetBoolean());

        // Nenhum processo `codex` órfão sobreviveu ao run.
        Assert.False(final.ProcessId is > 0 && IsAlive(final.ProcessId.Value));

        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task PublicStartResolvesPersonaToolsServerSideAndStopsBeforeExternalProcessWhenDisabled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CreateRepository();

        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(_root, "harness.db"),
            "--Harness:AgentRuns:Enabled", "true",
            "--Harness:AgentRuns:ControlledRoot", ControlledRoot,
            "--Harness:AgentRuns:AvailabilityLedgerPath",
                Path.Combine(Path.GetTempPath(), $"harness-availability-{Guid.NewGuid():N}.json"),
            "--Harness:AgentRuns:ProfilesRoot", Path.Combine(_root, "profiles"),
        ]);
        await app.StartAsync(timeout.Token);

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };
        var projectId = await SeedProjectAsync(client, timeout.Token);
        await UnsafeExecutionSetup.AcceptAsync(client, projectId, timeout.Token);
        var (taskId, attemptId) = await SeedTaskAndAttemptAsync(app, client, projectId, timeout.Token);

        // O card de documentação resolve a persona technical-writer no servidor. Ela declara
        // filesystem; desabilitar esse item pela API deve bloquear o POST real de agent-runs.
        using var toolsDocument = JsonDocument.Parse(
            await client.GetStringAsync("/api/v1/tools", timeout.Token));
        var filesystemId = toolsDocument.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "filesystem")
            .GetProperty("id").GetString()!;
        using (var disabled = await client.PatchAsJsonAsync(
            $"/api/v1/tools/{filesystemId}",
            new ComponentPatchRequest("disabled", null),
            timeout.Token))
        {
            disabled.EnsureSuccessStatusCode();
        }

        using var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId,
                attemptId,
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "Documente o frontend sem iniciar processo quando a ferramenta estiver desabilitada.",
            },
            timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "tool_disabled",
            await response.Content.ReadAsStringAsync(timeout.Token),
            StringComparison.Ordinal);
        Assert.Null(app.Services.GetRequiredService<AgentRunOrchestrator>().WaitAsync(attemptId));
        Assert.False(Directory.Exists(Path.Combine(ControlledRoot, "worktrees", attemptId)));

        await app.StopAsync(timeout.Token);
    }


    /// <summary>
    /// Semeia a cadeia real de trabalho: solicitação → demanda → tarefa → tentativa. O
    /// bootstrap opera sobre identidade DURÁVEL de domínio; ele não fabrica tarefa nem
    /// tentativa.
    /// </summary>
    private static async Task<(string TaskId, string AttemptId)> SeedTaskAndAttemptAsync(
        WebApplication app, HttpClient client, string projectId, CancellationToken token)
    {
        using var solicitation = await client.PostAsJsonAsync(
            "/api/v1/solicitations",
            new CreateSolicitationRequest(projectId, "request", "Cabeçalho do README", "Documentar o frontend."),
            token);
        solicitation.EnsureSuccessStatusCode();
        using var solicitationBody = JsonDocument.Parse(
            await solicitation.Content.ReadAsStringAsync(token));
        var solicitationId = solicitationBody.RootElement.GetProperty("id").GetString()!;

        using var demand = await client.PostAsJsonAsync(
            "/api/v1/demands",
            new CreateDemandRequest(projectId, "Documentar o frontend", "Adicionar cabeçalho.", solicitationId),
            token);
        demand.EnsureSuccessStatusCode();
        using var demandBody = JsonDocument.Parse(await demand.Content.ReadAsStringAsync(token));
        var demandId = demandBody.RootElement.GetProperty("id").GetString()!;

        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(
                projectId, "Documentar o frontend", "Adicione um cabeçalho ao README.", demandId),
            token);
        task.EnsureSuccessStatusCode();
        using var taskBody = JsonDocument.Parse(await task.Content.ReadAsStringAsync(token));
        var taskId = taskBody.RootElement.GetProperty("id").GetString()!;

        var board = app.Services.GetRequiredService<IWorkBoardStore>();
        var chain = app.Services.GetRequiredService<IWorkChainStore>();
        var tenantId = await ProfileTenantIdAsync(app);
        var persisted = (await board.GetTaskAsync(tenantId, taskId, token))!;
        var instruction = (await board.ListInstructionsAsync(tenantId, taskId, null, 10, token))[0];
        var attemptId = UlidValue.New(DateTimeOffset.UtcNow).ToString();

        var receipt = await chain.StartAttemptAsync(
            new WorkAttemptStartCommand(
                tenantId,
                persisted.BackingSolicitationId,
                taskId,
                instruction.Id,
                attemptId,
                "frontend-specialist",
                persisted.Version,
                $"agent-run-pilot:{attemptId}",
                DateTimeOffset.UtcNow),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, receipt.Status);
        return (taskId, attemptId);
    }

    private static async Task<string> ProfileTenantIdAsync(WebApplication app)
    {
        var profiles = await app.Services
            .GetRequiredService<ILocalProfileStore>()
            .ListAsync(CancellationToken.None);
        return profiles[0].TenantId;
    }

    private static string ResolveRealPath(string path)
    {
        var startInfo = new ProcessStartInfo("pwd")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-P");
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output.Length > 0 ? output : path;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Operador", null, null, "pt-BR"),
            token);
        profile.EnsureSuccessStatusCode();

        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        organization.EnsureSuccessStatusCode();
        using var organizationBody = JsonDocument.Parse(
            await organization.Content.ReadAsStringAsync(token));
        var organizationId = organizationBody.RootElement.GetProperty("id").GetString()!;

        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Projeto do piloto governado.",
                RepositoryUrl = RepositoryRoot,
            },
            token);
        project.EnsureSuccessStatusCode();
        using var projectBody = JsonDocument.Parse(
            await project.Content.ReadAsStringAsync(token));
        return projectBody.RootElement.GetProperty("id").GetString()!;
    }

    private void CreateRepository()
    {
        Directory.CreateDirectory(Path.Combine(RepositoryRoot, "frontend"));
        File.WriteAllText(Path.Combine(RepositoryRoot, "frontend", "README.md"), "# Frontend\n");
        Git("init --initial-branch=main");
        Git("config user.email operador@example.test");
        Git("config user.name Operador");
        Git("add .");
        Git("commit -m base");
    }

    private void Git(string arguments) => GitAsync(arguments).GetAwaiter().GetResult();

    private async Task<string> GitAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        return output;
    }

    private static Uri BaseAddress(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
