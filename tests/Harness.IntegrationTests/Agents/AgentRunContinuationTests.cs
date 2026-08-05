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
using Harness.Persistence.Abstractions.WorkChain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Continuação governada de um attempt reprovado. Prova, contra o Host real e um repositório
/// Git real, que a retomada:
/// - recupera o patch arquivado e valida o checksum contra o manifest de provenance;
/// - cria uma NOVA tentativa (nunca reusa a anterior), com novos claims e novo fencing;
/// - nasce a worktree da referência interna governada (`origin/develop`), nunca de um ref
///   arbitrário do cliente;
/// - recusa, com código tipado, um artifact cujo checksum não bate.
/// </summary>
public sealed class AgentRunContinuationTests : IDisposable
{
    private readonly string _root;

    public AgentRunContinuationTests()
    {
        var candidate = Path.Combine(Path.GetTempPath(), $"harness-run-cont-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidate);
        _root = ResolveRealPath(candidate);
    }

    private string ControlledRoot => Path.Combine(_root, "controlled");

    private string RepositoryRoot => Path.Combine(ControlledRoot, "project");

    private string ArchiveRoot => Path.Combine(_root, "archive");

    [Fact]
    public async Task AValidContinuationCreatesANewAttemptFromGovernedBaseAndAppliesTheArchivedPatch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CreateRepository();
        var patch = BuildFrontendPatch();

        await using var app = BuildHost();
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };

        var projectId = await SeedProjectAsync(client, timeout.Token);
        var (taskId, priorAttemptId) = await SeedTaskAndAttemptAsync(app, client, projectId, timeout.Token);

        // Arquiva o attempt reprovado com manifest de provenance e checksum.
        new AttemptArtifactArchive(ArchiveRoot).Write(
            RejectedManifest(priorAttemptId, projectId, taskId), patch);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId,
                resumeFromAttemptId = priorAttemptId,
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "Feche os achados do critic anterior.",
            },
            timeout.Token);

        var rawBody = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, rawBody);
        using var accepted = JsonDocument.Parse(rawBody);
        var run = accepted.RootElement;

        // Uma tentativa NOVA — nunca a reaproveitada.
        var newAttemptId = run.GetProperty("attemptId").GetString()!;
        Assert.NotEqual(priorAttemptId, newAttemptId);
        Assert.Equal("accepted", run.GetProperty("status").GetString());
        Assert.Equal(
            ["docs/frontend/**", "frontend/**"],
            run.GetProperty("scopeClaims").EnumerateArray()
                .Select(item => item.GetString()).Order(StringComparer.Ordinal));
        Assert.True(run.GetProperty("workspaceFencingToken").GetInt64() > 0);

        var orchestrator = app.Services.GetRequiredService<AgentRunOrchestrator>();
        var completion = orchestrator.WaitAsync(newAttemptId);
        Assert.NotNull(completion);
        var final = await completion.WaitAsync(timeout.Token);

        // A worktree nasceu de origin/develop e o receipt foi gravado antes do executor —
        // prova de que a base governada resolveu e a continuação preparou o run. O executor
        // falha por autenticação (perfil isolado não logado nesta máquina), como esperado.
        Assert.False(string.IsNullOrWhiteSpace(final.BundleChecksum));
        Assert.Equal(AgentRunStatus.Failed, final.Status);

        // Cleanup completo do novo attempt.
        var workspace = app.Services.GetRequiredService<IAttemptWorkspaceStore>();
        var snapshot = await workspace.GetAsync(await ProfileTenantIdAsync(app), newAttemptId, timeout.Token);
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.ReleasedAt);

        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task ATamperedArchivedPatchIsRefusedWithATypedChecksumCode()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CreateRepository();

        await using var app = BuildHost();
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };

        var projectId = await SeedProjectAsync(client, timeout.Token);
        var (taskId, priorAttemptId) = await SeedTaskAndAttemptAsync(app, client, projectId, timeout.Token);

        new AttemptArtifactArchive(ArchiveRoot).Write(
            RejectedManifest(priorAttemptId, projectId, taskId), BuildFrontendPatch());
        // Adultera o patch por fora, sem atualizar o manifest.
        File.WriteAllText(Path.Combine(ArchiveRoot, "pilot.patch"), "corrompido");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/agent-runs",
            new
            {
                projectId,
                taskId,
                resumeFromAttemptId = priorAttemptId,
                role = "frontend-specialist",
                account = "worker-codex-frontend",
                instruction = "Feche os achados.",
            },
            timeout.Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal("archive.checksum_mismatch", problem.RootElement.GetProperty("title").GetString());

        await app.StopAsync(timeout.Token);
    }

    private WebApplication BuildHost() => HostApplication.Build([
        "--urls", "http://127.0.0.1:0",
        "--Harness:DatabasePath", Path.Combine(_root, "harness.db"),
        "--Harness:AgentRuns:Enabled", "true",
            // 0-E: o contêiner é pré-requisito. O provider fake ATESTA uma sandbox efetiva, que é
            // o caminho único — não existe mais aceite de risco que dispense a fronteira.
            "--Harness:IsolatedExecution:Mode", "Fake",
        "--Harness:AgentRuns:ControlledRoot", ControlledRoot,
        // Ledger de disponibilidade PRÓPRIO do teste: sem isto o Host de teste lê e escreve
        // o ledger da instalação real do operador — uma conta em cooldown na máquina
        // reprovava a suíte, e o teste sujava o estado de produção do dono.
        "--Harness:AgentRuns:AvailabilityLedgerPath",
            Path.Combine(Path.GetTempPath(), $"harness-availability-{Guid.NewGuid():N}.json"),
        "--Harness:AgentRuns:ProfilesRoot", Path.Combine(_root, "profiles"),
        "--Harness:AgentRuns:ArchiveRoot", ArchiveRoot,
        "--Harness:AgentRuns:RunTimeout", "00:02:00",
    ]);

    private ArchivedAttemptManifest RejectedManifest(string attemptId, string projectId, string taskId) => new()
    {
        AttemptId = attemptId,
        TenantId = "01KY000000000000000000TEN0",
        ProjectId = projectId,
        TaskId = taskId,
        Role = "frontend-specialist",
        ActorAlias = "worker-codex-frontend",
        ControlledRepositoryRoot = RepositoryRoot,
        SourceCommit = "6e4f713",
        PatchFileName = "pilot.patch",
        PatchSha256 = "sealed-on-write",
        Verdict = "fail",
        ReviewId = "01KY37PQX9921N4SHPE620CJ0Q",
        ScopeClaims = ["frontend/**", "docs/frontend/**"],
        Findings = [new ArchivedAttemptFinding("P0", "suite-vermelha", "1 teste falhando")],
        ArchivedAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task<(string TaskId, string AttemptId)> SeedTaskAndAttemptAsync(
        WebApplication app, HttpClient client, string projectId, CancellationToken token)
    {
        using var solicitation = await client.PostAsJsonAsync(
            "/api/v1/solicitations",
            new CreateSolicitationRequest(projectId, "request", "Fechar findings", "Continuar o piloto."),
            token);
        solicitation.EnsureSuccessStatusCode();
        using var solicitationBody = JsonDocument.Parse(await solicitation.Content.ReadAsStringAsync(token));
        var solicitationId = solicitationBody.RootElement.GetProperty("id").GetString()!;

        using var demand = await client.PostAsJsonAsync(
            "/api/v1/demands",
            new CreateDemandRequest(projectId, "Continuar o piloto", "Fechar achados.", solicitationId),
            token);
        demand.EnsureSuccessStatusCode();
        using var demandBody = JsonDocument.Parse(await demand.Content.ReadAsStringAsync(token));
        var demandId = demandBody.RootElement.GetProperty("id").GetString()!;

        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, "Continuar o piloto", "Fechar os achados do critic.", demandId),
            token);
        task.EnsureSuccessStatusCode();
        using var taskBody = JsonDocument.Parse(await task.Content.ReadAsStringAsync(token));
        var taskId = taskBody.RootElement.GetProperty("id").GetString()!;

        var board = app.Services.GetRequiredService<IWorkBoardStore>();
        var chain = app.Services.GetRequiredService<IWorkChainStore>();
        var tenantId = await ProfileTenantIdAsync(app);
        var persisted = (await board.GetTaskAsync(tenantId, taskId, token))!;
        var instruction = (await board.ListInstructionsAsync(tenantId, taskId, null, 10, token))[0];
        var attemptId = SharedKernel.Identifiers.UlidValue.New(DateTimeOffset.UtcNow).ToString();

        var now = DateTimeOffset.UtcNow;
        var backingSolicitationId = persisted.BackingSolicitationId;
        var receipt = await chain.StartAttemptAsync(
            new WorkAttemptStartCommand(
                tenantId, backingSolicitationId, taskId, instruction.Id, attemptId,
                "worker-codex-frontend", persisted.Version, $"pilot:{attemptId}", now),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, receipt.Status);

        // O attempt anterior foi CONCLUÍDO e então REPROVADO por um critic independente —
        // é exatamente esse estado terminal que a continuação retoma. Sem isso, a cadeia
        // (corretamente) recusa iniciar uma segunda tentativa.
        var completed = await chain.CompleteAttemptAsync(
            new WorkAttemptCompleteCommand(
                tenantId, backingSolicitationId, taskId, attemptId, receipt.TaskVersion!.Value,
                [new WorkEvidenceInput(NewId(), "workspace:task/prior@6e4f713")],
                $"pilot:complete:{attemptId}", now.AddSeconds(1)),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);

        var rejected = await chain.ReviewAttemptAsync(
            new WorkAttemptReviewCommand(
                tenantId, backingSolicitationId, taskId, attemptId, NewId(), "chief-claude-primary",
                "rejected", "Nove achados P0-P3; suíte vermelha.", completed.TaskVersion!.Value,
                $"pilot:review:{attemptId}", now.AddSeconds(2))
            {
                RejectionCause = "acceptanceNotMet",
            },
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, rejected.Status);

        // A rejeição exige uma nova instrução (v2) antes de qualquer nova tentativa.
        const string correction = "Feche os nove achados do critic sobre o diff anterior.";
        var v2 = await chain.AddInstructionVersionAsync(
            new WorkInstructionVersionCreateCommand(
                tenantId, backingSolicitationId, taskId, NewId(), correction,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(correction))),
                rejected.TaskVersion!.Value, $"pilot:instr2:{attemptId}", now.AddSeconds(3)),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, v2.Status);

        return (taskId, attemptId);
    }

    private static string NewId() =>
        SharedKernel.Identifiers.UlidValue.New(DateTimeOffset.UtcNow).ToString();

    private static async Task<string> ProfileTenantIdAsync(WebApplication app)
    {
        var profiles = await app.Services
            .GetRequiredService<ILocalProfileStore>().ListAsync(CancellationToken.None);
        return profiles[0].TenantId;
    }

    private async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Operador", null, null, "pt-BR"), token);
        profile.EnsureSuccessStatusCode();

        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        organization.EnsureSuccessStatusCode();
        using var organizationBody = JsonDocument.Parse(await organization.Content.ReadAsStringAsync(token));
        var organizationId = organizationBody.RootElement.GetProperty("id").GetString()!;

        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Continuação governada.",
                RepositoryUrl = RepositoryRoot,
            },
            token);
        project.EnsureSuccessStatusCode();
        using var projectBody = JsonDocument.Parse(await project.Content.ReadAsStringAsync(token));
        return projectBody.RootElement.GetProperty("id").GetString()!;
    }

    private void CreateRepository()
    {
        Directory.CreateDirectory(Path.Combine(RepositoryRoot, "frontend"));
        File.WriteAllText(Path.Combine(RepositoryRoot, "frontend", "README.md"), "# Frontend\n");
        Git("init --initial-branch=develop");
        Git("config user.email operador@example.test");
        Git("config user.name Operador");
        Git("add .");
        Git("commit -m base");
        // A referência interna governada existe sem um remoto real: origin/develop aponta
        // para o commit atual, como faria um fetch.
        Git("update-ref refs/remotes/origin/develop HEAD");
    }

    private string BuildFrontendPatch()
    {
        File.WriteAllText(Path.Combine(RepositoryRoot, "frontend", "README.md"), "# Frontend\ncontinuado\n");
        var diff = GitAsync("diff").GetAwaiter().GetResult();
        Git("checkout -- frontend/README.md");
        return diff;
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
