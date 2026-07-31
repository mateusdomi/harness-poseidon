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
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Execution.Infrastructure.Git;
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

namespace Harness.RecoveryTests;

public sealed class IsolatedAttemptRecoveryTests
{
    [Fact]
    public async Task ReconcilesEveryCompositionStageAfterAbruptTermination()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "recovery-artifacts",
            $"isolated-attempts-{Guid.NewGuid():N}");
        var controlledRoot = Path.Combine(root, "controlled");
        var repository = Path.Combine(controlledRoot, "external-repository");
        var database = Path.Combine(root, "recovery.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(controlledRoot);
        await CreateFixtureRepositoryAsync(repository, timeout.Token);
        var crashedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        try
        {
            await using var app = CreateHost(database);
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
                var store = app.Services.GetRequiredService<IAttemptWorkspaceStore>();
                var manager = await GitWorktreeManager.OpenAsync(
                    repository,
                    controlledRoot,
                    timeout.Token);
                var sandbox = new RecordingSandboxProvider();
                var orchestrator = new IsolatedAttemptOrchestrator(
                    store,
                    sandbox,
                    new FakeSandboxExecutorFactory(),
                    SystemClock.Instance,
                    new IsolatedExecutionOptions
                    {
                        AgentImageName = "harness-fake-agent:latest",
                        ProxyImageName = "harness-fake-proxy:latest",
                        ProxyCommand = "proxy",
                        ContainerExecutable = "codex",
                        HeartbeatInterval = TimeSpan.FromMilliseconds(50),
                    });

                async Task<CrashedStage> StageAsync(
                    string title,
                    string slug,
                    IReadOnlyList<string> claims)
                {
                    var (taskId, attemptId) = await CreateAttemptAsync(
                        app.Services,
                        client,
                        tenantId,
                        project.Id,
                        title,
                        DateTimeOffset.UtcNow,
                        timeout.Token);
                    var worktree = Path.Combine(controlledRoot, "worktrees", slug);
                    var acquire = await store.AcquireAsync(
                        new AttemptWorkspaceAcquireCommand
                        {
                            TenantId = tenantId,
                            ProjectId = project.Id,
                            TaskId = taskId,
                            AttemptId = attemptId,
                            RepositoryRoot = repository,
                            ControlledRoot = controlledRoot,
                            BaseReference = "HEAD",
                            BranchName = $"task/{slug}",
                            WorktreePath = worktree,
                            ScopeClaims = claims,
                            Owner = "crashed-runner",
                            LeaseDuration = TimeSpan.FromMinutes(1),
                            IdempotencyKey = $"recovery-{slug}",
                            OccurredAt = crashedAt,
                        },
                        timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, acquire.Status);
                    return new CrashedStage(taskId, attemptId, $"task/{slug}", worktree, slug);
                }

                async Task<AttemptWorkspaceSnapshot> TransitionAsync(
                    CrashedStage stage,
                    AttemptWorkspaceState expected,
                    AttemptWorkspaceState state,
                    string? commitSha = null,
                    string? sessionId = null)
                {
                    var receipt = await store.TransitionAsync(
                        new AttemptWorkspaceTransitionCommand
                        {
                            TenantId = tenantId,
                            AttemptId = stage.AttemptId,
                            Owner = "crashed-runner",
                            FencingToken = 1,
                            ExpectedState = expected,
                            State = state,
                            CommitSha = commitSha,
                            SessionId = sessionId,
                            OccurredAt = crashedAt.AddSeconds(1),
                        },
                        timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, receipt.Status);
                    return receipt.Workspace!;
                }

                StartIsolatedExecutionCommand ResumeCommand(CrashedStage stage) => new()
                {
                    TenantId = tenantId,
                    ProjectId = project.Id,
                    TaskId = stage.TaskId,
                    AttemptId = stage.AttemptId,
                    ConversationId = UlidValue.New(DateTimeOffset.UtcNow).ToString(),
                    AgentId = "software-engineer",
                    Instruction = "Resume the interrupted isolated attempt.",
                    StatusDigestJson = """{"phase":"recovery"}""",
                    RepositoryRoot = repository,
                    ControlledRoot = controlledRoot,
                    BaseReference = "HEAD",
                    BranchName = stage.BranchName,
                    WorktreePath = stage.WorktreePath,
                    ScopeClaims = ["src/" + stage.Slug + "/**"],
                    Owner = "recovery-runner",
                    LeaseDuration = TimeSpan.FromMinutes(1),
                    IdempotencyKey = $"recovery-{stage.Slug}",
                };

                // Estágio 1: morte após o claim e antes da branch.
                var afterClaim = await StageAsync(
                    "Crash after claim",
                    "after-claim",
                    ["src/after-claim/**"]);
                var resumedClaim = await orchestrator.ExecuteAsync(
                    ResumeCommand(afterClaim),
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, resumedClaim.Status);
                Assert.NotNull(resumedClaim.Execution);
                Assert.Equal("recovery-runner", resumedClaim.Workspace!.Owner);
                Assert.Equal(2, resumedClaim.Workspace.FencingToken);
                Assert.Equal(
                    AttemptWorkspaceCleanupState.Completed,
                    resumedClaim.Workspace.CleanupState);

                // Estágio 2: morte após branch/worktree e antes do sandbox.
                var afterWorktree = await StageAsync(
                    "Crash after worktree",
                    "after-worktree",
                    ["src/after-worktree/**"]);
                var createdDescriptor = await manager.CreateTaskWorktreeAsync(
                    afterWorktree.BranchName,
                    afterWorktree.AttemptId,
                    afterWorktree.WorktreePath,
                    "HEAD",
                    timeout.Token);
                await TransitionAsync(
                    afterWorktree,
                    AttemptWorkspaceState.Claimed,
                    AttemptWorkspaceState.Prepared,
                    commitSha: createdDescriptor.HeadCommit);
                var resumedWorktree = await orchestrator.ExecuteAsync(
                    ResumeCommand(afterWorktree),
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, resumedWorktree.Status);
                Assert.Equal(createdDescriptor.HeadCommit, resumedWorktree.Workspace!.CommitSha);
                Assert.Equal(2, resumedWorktree.Workspace.FencingToken);

                // Estágio 3: morte durante o executor.
                var duringExecutor = await StageAsync(
                    "Crash during executor",
                    "during-executor",
                    ["src/during-executor/**"]);
                var executorDescriptor = await manager.CreateTaskWorktreeAsync(
                    duringExecutor.BranchName,
                    duringExecutor.AttemptId,
                    duringExecutor.WorktreePath,
                    "HEAD",
                    timeout.Token);
                await TransitionAsync(
                    duringExecutor,
                    AttemptWorkspaceState.Claimed,
                    AttemptWorkspaceState.Prepared,
                    commitSha: executorDescriptor.HeadCommit);
                await TransitionAsync(
                    duringExecutor,
                    AttemptWorkspaceState.Prepared,
                    AttemptWorkspaceState.Running,
                    sessionId: "sandbox:crashed-session");
                var resumedExecutor = await orchestrator.ExecuteAsync(
                    ResumeCommand(duringExecutor),
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, resumedExecutor.Status);
                Assert.NotNull(resumedExecutor.Execution);
                Assert.Equal(
                    resumedExecutor.Execution!.SessionId,
                    resumedExecutor.Workspace!.SessionId);

                // Estágio 4: morte após a conclusão e antes do cleanup.
                var beforeCleanup = await StageAsync(
                    "Crash before cleanup",
                    "before-cleanup",
                    ["src/before-cleanup/**"]);
                var cleanupDescriptor = await manager.CreateTaskWorktreeAsync(
                    beforeCleanup.BranchName,
                    beforeCleanup.AttemptId,
                    beforeCleanup.WorktreePath,
                    "HEAD",
                    timeout.Token);
                await TransitionAsync(
                    beforeCleanup,
                    AttemptWorkspaceState.Claimed,
                    AttemptWorkspaceState.Prepared,
                    commitSha: cleanupDescriptor.HeadCommit);
                await TransitionAsync(
                    beforeCleanup,
                    AttemptWorkspaceState.Prepared,
                    AttemptWorkspaceState.Running,
                    sessionId: "sandbox:crashed-session");
                await TransitionAsync(
                    beforeCleanup,
                    AttemptWorkspaceState.Running,
                    AttemptWorkspaceState.Completed);
                var resumedCleanup = await orchestrator.ExecuteAsync(
                    ResumeCommand(beforeCleanup),
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, resumedCleanup.Status);
                Assert.Null(resumedCleanup.Execution);
                Assert.Equal(
                    AttemptWorkspaceCleanupState.Completed,
                    resumedCleanup.Workspace!.CleanupState);
                Assert.False(Directory.Exists(beforeCleanup.WorktreePath));
                Assert.Equal(
                    0,
                    sandbox.OpenCounts.GetValueOrDefault(
                        IsolatedAttemptOrchestrator.SandboxAttemptLabel(beforeCleanup.AttemptId)));

                // Estágio 5: morte durante cleanup parcial (worktree já removida).
                var partialCleanup = await StageAsync(
                    "Crash during partial cleanup",
                    "partial-cleanup",
                    ["src/partial-cleanup/**"]);
                var partialDescriptor = await manager.CreateTaskWorktreeAsync(
                    partialCleanup.BranchName,
                    partialCleanup.AttemptId,
                    partialCleanup.WorktreePath,
                    "HEAD",
                    timeout.Token);
                await TransitionAsync(
                    partialCleanup,
                    AttemptWorkspaceState.Claimed,
                    AttemptWorkspaceState.Prepared,
                    commitSha: partialDescriptor.HeadCommit);
                await TransitionAsync(
                    partialCleanup,
                    AttemptWorkspaceState.Prepared,
                    AttemptWorkspaceState.Running,
                    sessionId: "sandbox:crashed-session");
                await TransitionAsync(
                    partialCleanup,
                    AttemptWorkspaceState.Running,
                    AttemptWorkspaceState.Completed);
                Assert.True(await manager.RemoveTaskWorktreeAsync(
                    partialCleanup.BranchName,
                    partialCleanup.WorktreePath,
                    deleteBranch: false,
                    timeout.Token));
                var resumedPartial = await orchestrator.ExecuteAsync(
                    ResumeCommand(partialCleanup),
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, resumedPartial.Status);
                Assert.Null(resumedPartial.Execution);
                Assert.NotNull(resumedPartial.Workspace!.ReleasedAt);
                Assert.All(
                    resumedPartial.Workspace.ScopeClaims,
                    claim => Assert.NotNull(claim.ReleasedAt));

                // Estágio 6: lease ativa de outro owner não pode ser roubada.
                var activeOwner = await StageAsync(
                    "Active owner keeps the claim",
                    "active-owner",
                    ["src/active-owner/**"]);
                var refreshed = await store.HeartbeatAsync(
                    new AttemptWorkspaceLeaseCommand(
                        tenantId,
                        activeOwner.AttemptId,
                        "crashed-runner",
                        1,
                        TimeSpan.FromMinutes(10),
                        DateTimeOffset.UtcNow),
                    timeout.Token);
                Assert.Equal(AttemptWorkspaceMutationStatus.Applied, refreshed.Status);
                var rejected = await orchestrator.ExecuteAsync(
                    ResumeCommand(activeOwner),
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Rejected, rejected.Status);
                var untouched = await store.GetAsync(tenantId, activeOwner.AttemptId, timeout.Token);
                Assert.Equal("crashed-runner", untouched!.Owner);
                Assert.Equal(1, untouched.FencingToken);
                Assert.Equal(AttemptWorkspaceState.Claimed, untouched.State);
                Assert.DoesNotContain(
                    activeOwner.BranchName,
                    await manager.ListLocalBranchesAsync(timeout.Token));

                // Invariantes globais: nenhuma branch/worktree duplicada e nenhum recurso sandbox restante.
                var branches = await manager.ListLocalBranchesAsync(timeout.Token);
                Assert.Equal(branches.Distinct(StringComparer.Ordinal).Count(), branches.Count);
                Assert.Single(await manager.ListWorktreesAsync(timeout.Token));
                Assert.All(
                    sandbox.OpenCounts,
                    entry => Assert.True(
                        sandbox.CleanupCounts.GetValueOrDefault(entry.Key) >= 1,
                        $"Sandbox {entry.Key} was opened without cleanup."));
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

    private sealed record CrashedStage(
        string TaskId,
        string AttemptId,
        string BranchName,
        string WorktreePath,
        string Slug);

    private static async Task CreateFixtureRepositoryAsync(string repository, CancellationToken token)
    {
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, ["init", "--initial-branch=main"], token);
        await RunGitAsync(repository, ["config", "user.email", "fixture@harness.local"], token);
        await RunGitAsync(repository, ["config", "user.name", "Harness Fixture"], token);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "fixture", token);
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
            new CreateTaskRequest(projectId, title, "Recover inside the isolated worktree."),
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
        var started = await chain.StartAttemptAsync(new(
            tenantId,
            persisted.BackingSolicitationId,
            task.Id,
            instruction.Id,
            attemptId,
            "software-engineer",
            persisted.Version,
            $"recovery-test:{attemptId}",
            at), token);
        Assert.Equal(WorkChainMutationStatus.Applied, started.Status);
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

    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    private sealed class RecordingSandboxProvider : ISandboxProvider
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

        public Dictionary<string, int> OpenCounts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> CleanupCounts { get; } = new(StringComparer.Ordinal);

        public Task<ISandboxProcessSession> OpenProcessSessionAsync(
            SandboxProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenCounts[request.AttemptId] = OpenCounts.GetValueOrDefault(request.AttemptId) + 1;
            return Task.FromResult<ISandboxProcessSession>(new RecordingSession(this, request.AttemptId));
        }

        public Task<SandboxRunResult> RunAsync(
            SandboxRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The recovery test uses process sessions only.");

        public Task<SandboxResourceInventory> DetectResourcesAsync(
            string attemptId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxResourceInventory([], [], [], []));

        public Task CleanupAsync(string attemptId, CancellationToken cancellationToken = default)
        {
            CleanupCounts[attemptId] = CleanupCounts.GetValueOrDefault(attemptId) + 1;
            return Task.CompletedTask;
        }

        private sealed class RecordingSession(RecordingSandboxProvider provider, string attemptId)
            : ISandboxProcessSession
        {
            public SandboxProcessPlan ProcessPlan { get; } = new(
                "/usr/bin/true",
                [],
                "/workspace",
                RootFilesystemReadOnly: true,
                WorktreeIsolated: true,
                EgressRestricted: true,
                ResourceLimitsApplied: true);

            public ValueTask DisposeAsync()
            {
                provider.CleanupCounts[attemptId] =
                    provider.CleanupCounts.GetValueOrDefault(attemptId) + 1;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeSandboxExecutorFactory : ISandboxAgentExecutorFactory
    {
        public IAgentExecutor Create(SandboxProcessPlan plan, StartIsolatedExecutionCommand command) =>
            new FakeAgentExecutor();
    }
}
