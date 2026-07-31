using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Execution;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Smoke classificado com agente de IA real (CLI local autenticada), executado somente
/// com HARNESS_RUN_REAL_AGENT_TESTS=true. Usa a CLI configurada em
/// HARNESS_REAL_AGENT_CLI (padrão: agy) diretamente na worktree do host — modo
/// registrado como inseguro/aceito para smoke, sem sandbox Docker do Harness.
/// </summary>
public sealed class RealAgentSmokeTests
{
    private static readonly string[] DogfoodClaims = ["src/**"];

    [Fact]
    public async Task RealAgentProducesWorkInsideIsolatedAttemptWorktree()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HARNESS_RUN_REAL_AGENT_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"real-agent-{Guid.NewGuid():N}");
        var controlledRoot = Path.Combine(root, "controlled");
        var repository = Path.Combine(controlledRoot, "external-repository");
        var database = Path.Combine(root, "real-agent.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(controlledRoot);
        await CreateFixtureRepositoryAsync(repository, timeout.Token);

        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var profile = await CreateProfileAsync(client, timeout.Token);
                var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                    .GetAsync(profile.Id, timeout.Token))!.TenantId;
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                var (taskId, attemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    project.Id,
                    "Entrega real via agente local",
                    DateTimeOffset.UtcNow,
                    timeout.Token);

                var orchestrator = new IsolatedAttemptOrchestrator(
                    app.Services.GetRequiredService<IAttemptWorkspaceStore>(),
                    new HostPassthroughSandboxProvider(),
                    new RealCliExecutorFactory(),
                    SystemClock.Instance,
                    new IsolatedExecutionOptions
                    {
                        AgentImageName = "harness-unused:latest",
                        ProxyImageName = "harness-unused:latest",
                        ProxyCommand = "proxy",
                        ContainerExecutable = "unused",
                        HeartbeatInterval = TimeSpan.FromSeconds(5),
                    });
                var worktree = Path.Combine(controlledRoot, "worktrees", "real-agent");
                var result = await orchestrator.ExecuteAsync(
                    new StartIsolatedExecutionCommand
                    {
                        TenantId = tenantId,
                        ProjectId = project.Id,
                        TaskId = taskId,
                        AttemptId = attemptId,
                        ConversationId = attemptId,
                        AgentId = "software-engineer",
                        Instruction =
                            "Crie um arquivo chamado STATUS.md na raiz do diretório de trabalho " +
                            "atual com exatamente o conteúdo: agente-real-ok",
                        StatusDigestJson = """{"phase":"smoke"}""",
                        RepositoryRoot = repository,
                        ControlledRoot = controlledRoot,
                        BaseReference = "HEAD",
                        BranchName = "task/real-agent-smoke",
                        WorktreePath = worktree,
                        ScopeClaims = DogfoodClaims,
                        Owner = "real-agent-smoke",
                        LeaseDuration = TimeSpan.FromMinutes(10),
                        IdempotencyKey = "real-agent-smoke",
                    },
                    timeout.Token);

                Assert.Equal(IsolatedExecutionStatus.Completed, result.Status);
                Assert.NotNull(result.Execution);
                Assert.Equal("real-cli", result.Execution!.Executor);
                Assert.False(string.IsNullOrWhiteSpace(result.Execution.StructuredOutput));

                // O agente real deixou trabalho não commitado: a worktree suja é preservada
                // com o claim retido (cleanup pendente) — mesma política do fluxo Codex.
                var statusFile = Path.Combine(worktree, "STATUS.md");
                Assert.True(File.Exists(statusFile), "O agente real não criou STATUS.md.");
                Assert.Contains(
                    "agente-real-ok",
                    await File.ReadAllTextAsync(statusFile, timeout.Token));
                Assert.Equal(
                    AttemptWorkspaceCleanupState.Pending,
                    result.Workspace!.CleanupState);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class RealCliExecutorFactory : ISandboxAgentExecutorFactory
    {
        public IAgentExecutor Create(SandboxProcessPlan plan, StartIsolatedExecutionCommand command) =>
            new RealCliAgentExecutor();
    }

    private sealed class RealCliAgentExecutor : IAgentExecutor
    {
        public async Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var started = Stopwatch.GetTimestamp();
            var cli = Environment.GetEnvironmentVariable("HARNESS_REAL_AGENT_CLI") ?? "agy";
            var prompt =
                $"{request.Instruction}\n\n" +
                "Trabalhe somente dentro do diretório de trabalho atual. " +
                "Ao terminar, responda SOMENTE com um JSON válido no formato " +
                "{\"response\": \"<resumo curto>\", \"demands\": []} e nada mais.";
            var output = await RunAsync(cli, prompt, request.WorkingDirectory, cancellationToken);
            var structured = ExtractJson(output);
            try
            {
                _ = ChiefTurnOutputContract.Parse(structured);
            }
            catch (AgentOutputValidationException)
            {
                var repaired = await RunAsync(
                    cli,
                    "Reformate a resposta a seguir para SOMENTE um JSON válido no formato " +
                    "{\"response\": string, \"demands\": []} e nada mais:\n" + output,
                    request.WorkingDirectory,
                    cancellationToken);
                structured = ExtractJson(repaired);
                _ = ChiefTurnOutputContract.Parse(structured);
            }

            return new AgentExecutionResult(
                "real-cli",
                request.SessionId ?? $"real-cli:{request.ConversationId}",
                $"real-cli:{Guid.NewGuid():N}",
                structured,
                [structured],
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        private static async Task<string> RunAsync(
            string cli,
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(cli)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(prompt);
            startInfo.ArgumentList.Add("--dangerously-skip-permissions");
            startInfo.ArgumentList.Add("--add-dir");
            startInfo.ArgumentList.Add(workingDirectory);
            startInfo.ArgumentList.Add("--print-timeout");
            startInfo.ArgumentList.Add("4m");
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The real agent CLI did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The real agent CLI failed ({process.ExitCode}): {await stderr}");
            }

            return (await stdout).Trim();
        }

        private static string ExtractJson(string output)
        {
            var start = output.IndexOf('{', StringComparison.Ordinal);
            var end = output.LastIndexOf('}');
            return start >= 0 && end > start
                ? output[start..(end + 1)]
                : output;
        }
    }

    private sealed class HostPassthroughSandboxProvider : ISandboxProvider
    {
        /// <summary>
        /// Fase 0B1: um provider de teste atesta explicitamente o que ele é. Devolver "sandbox
        /// ativa" por conveniência aqui reproduziria em teste exatamente a mentira que o bloco
        /// existe para eliminar em produção.
        /// </summary>
        public Task<SandboxAttestation> AttestAsync(
            SandboxAttestationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(new SandboxAttestation(
                request.TenantId, request.ProjectId, request.AttemptId, "test", "1",
                $"test-sandbox:{request.AttemptId}", ["/workspace"], "denied",
                RootFilesystemReadOnly: true, WorktreeIsolated: true, EgressRestricted: true,
                ResourceLimitsApplied: true, Verified: true,
                "Test double: boundaries are asserted by the test, not by a runtime.",
                request.IssuedAt));
        }

        public Task<ISandboxProcessSession> OpenProcessSessionAsync(
            SandboxProcessRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ISandboxProcessSession>(new PassthroughSession());

        public Task<SandboxRunResult> RunAsync(
            SandboxRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The smoke uses process sessions only.");

        public Task<SandboxResourceInventory> DetectResourcesAsync(
            string attemptId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxResourceInventory([], [], [], []));

        public Task CleanupAsync(string attemptId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private sealed class PassthroughSession : ISandboxProcessSession
        {
            public SandboxProcessPlan ProcessPlan { get; } = new(
                "/usr/bin/true",
                [],
                "/workspace",
                RootFilesystemReadOnly: true,
                WorktreeIsolated: true,
                EgressRestricted: true,
                ResourceLimitsApplied: true);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task CreateFixtureRepositoryAsync(string repository, CancellationToken token)
    {
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, ["init", "--initial-branch=main"], token);
        await RunGitAsync(repository, ["config", "user.email", "fixture@harness.local"], token);
        await RunGitAsync(repository, ["config", "user.name", "Harness Fixture"], token);
        await File.WriteAllTextAsync(
            Path.Combine(repository, "README.md"),
            "fixture de smoke com agente real",
            token);
        await RunGitAsync(repository, ["add", "README.md"], token);
        await RunGitAsync(repository, ["commit", "-m", "bootstrap"], token);
    }

    private static async Task RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken token)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {await process.StandardError.ReadToEndAsync(token)}");
        }
    }

    private static async Task<(string TaskId, string AttemptId)> CreateAttemptAsync(
        IServiceProvider services,
        HttpClient client,
        string tenantId,
        string projectId,
        string title,
        DateTimeOffset at,
        CancellationToken token)
    {
        using var taskResponse = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, title, "Executar com agente real no smoke."),
            token);
        taskResponse.EnsureSuccessStatusCode();
        var task = (await taskResponse.Content.ReadFromJsonAsync<BoardTaskContract>(token))!;
        var board = services.GetRequiredService<IWorkBoardStore>();
        var chain = services.GetRequiredService<IWorkChainStore>();
        var persisted = (await board.GetTaskAsync(tenantId, task.Id, token))!;
        var instruction = Assert.Single(await board.ListInstructionsAsync(
            tenantId,
            task.Id,
            null,
            10,
            token));
        var attemptId = UlidValue.New(at).ToString();
        var startedReceipt = await chain.StartAttemptAsync(new(
            tenantId,
            persisted.BackingSolicitationId,
            task.Id,
            instruction.Id,
            attemptId,
            "software-engineer",
            persisted.Version,
            $"real-agent-smoke:{attemptId}",
            at), token);
        Assert.Equal(WorkChainMutationStatus.Applied, startedReceipt.Status);
        return (task.Id, attemptId);
    }

    private static async Task<ProfileResponse> CreateProfileAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"),
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(
        HttpClient client,
        string organizationId,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "POSEIDON",
                Description = "Backend",
            },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
