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
using Harness.Modules.Governance.Coordination;
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

namespace Harness.IntegrationTests.Git;

public sealed class IsolatedAttemptOrchestrationTests
{
    [Fact]
    public async Task ComposesClaimWorktreeSandboxAndExecutorWithDurableLifecycleAndCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"isolated-orchestration-{Guid.NewGuid():N}");
        var controlledRoot = Path.Combine(root, "controlled");
        var repository = Path.Combine(controlledRoot, "external-repository");
        var database = Path.Combine(root, "orchestration.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(controlledRoot);
        await CreateFixtureRepositoryAsync(repository, timeout.Token);

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
                var (successTaskId, successAttemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    project.Id,
                    "Deliver the isolated feature",
                    DateTimeOffset.UtcNow,
                    timeout.Token);
                var (failureTaskId, failureAttemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    project.Id,
                    "Fail inside the executor",
                    DateTimeOffset.UtcNow.AddMilliseconds(10),
                    timeout.Token);

                var store = app.Services.GetRequiredService<IAttemptWorkspaceStore>();
                var sandbox = new RecordingSandboxProvider();
                var options = new IsolatedExecutionOptions
                {
                    AgentImageName = "harness-fake-agent:latest",
                    ProxyImageName = "harness-fake-proxy:latest",
                    ProxyCommand = "proxy",
                    ContainerExecutable = "codex",
                    HeartbeatInterval = TimeSpan.FromMilliseconds(50),
                };
                var orchestrator = new IsolatedAttemptOrchestrator(
                    store,
                    sandbox,
                    new FakeSandboxExecutorFactory(() => new FakeAgentExecutor()),
                    SystemClock.Instance,
                    options);

                var successCommand = CreateCommand(
                    tenantId,
                    project.Id,
                    successTaskId,
                    successAttemptId,
                    repository,
                    controlledRoot,
                    Path.Combine(controlledRoot, "worktrees", "success"),
                    "task/deliver-isolated-feature",
                    ["src/feature/**"],
                    "orchestrator-1",
                    "orchestrate-success");
                var completed = await orchestrator.ExecuteAsync(successCommand, timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, completed.Status);
                Assert.NotNull(completed.Execution);
                Assert.Equal("fake", completed.Execution!.Executor);
                Assert.Equal(AttemptWorkspaceState.Completed, completed.Workspace!.State);
                Assert.Equal(AttemptWorkspaceCleanupState.Completed, completed.Workspace.CleanupState);
                Assert.Equal(completed.Execution.SessionId, completed.Workspace.SessionId);
                Assert.NotNull(completed.Workspace.CommitSha);
                Assert.Equal(40, completed.Workspace.CommitSha!.Length);
                Assert.NotNull(completed.Workspace.ReleasedAt);
                Assert.All(completed.Workspace.ScopeClaims, claim => Assert.NotNull(claim.ReleasedAt));
                Assert.False(Directory.Exists(successCommand.WorktreePath));
                var manager = await GitWorktreeManager.OpenAsync(repository, controlledRoot, timeout.Token);
                Assert.Contains(
                    "task/deliver-isolated-feature",
                    await manager.ListLocalBranchesAsync(timeout.Token));
                Assert.Single(await manager.ListWorktreesAsync(timeout.Token));
                var successLabel = IsolatedAttemptOrchestrator.SandboxAttemptLabel(successAttemptId);
                Assert.Equal(1, sandbox.OpenCounts.GetValueOrDefault(successLabel));
                Assert.True(sandbox.CleanupCounts.GetValueOrDefault(successLabel) >= 1);

                var replay = await orchestrator.ExecuteAsync(successCommand, timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, replay.Status);
                Assert.Null(replay.Execution);
                Assert.Equal(AttemptWorkspaceCleanupState.Completed, replay.Workspace!.CleanupState);
                Assert.Equal(1, sandbox.OpenCounts.GetValueOrDefault(successLabel));

                var denied = await orchestrator.ExecuteAsync(
                    successCommand with
                    {
                        EnforcePoseidonPathPolicy = true,
                        PathScopeKind = AgentPathScopeKind.Backend,
                        ScopeClaims = ["frontend/**"],
                        IdempotencyKey = "orchestrate-denied-scope",
                    },
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Rejected, denied.Status);
                Assert.Equal("agent_path_scope_denied", denied.FinalError);
                Assert.Null(denied.Workspace);
                Assert.Equal(1, sandbox.OpenCounts.GetValueOrDefault(successLabel));

                var failureCommand = CreateCommand(
                    tenantId,
                    project.Id,
                    failureTaskId,
                    failureAttemptId,
                    repository,
                    controlledRoot,
                    Path.Combine(controlledRoot, "worktrees", "failure"),
                    "task/fail-inside-executor",
                    ["src/failing/**"],
                    "orchestrator-2",
                    "orchestrate-failure");
                var failed = await orchestrator.ExecuteAsync(
                    failureCommand with
                    {
                        Instruction = "boom",
                    },
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Failed, failed.Status);
                Assert.NotNull(failed.FinalError);
                Assert.Contains("ThrowingAgentException", failed.FinalError);
                Assert.Equal(AttemptWorkspaceState.Failed, failed.Workspace!.State);
                Assert.Equal(failed.FinalError, failed.Workspace.FinalError);
                Assert.Equal(AttemptWorkspaceCleanupState.Completed, failed.Workspace.CleanupState);
                Assert.False(Directory.Exists(failureCommand.WorktreePath));
                Assert.Single(await manager.ListWorktreesAsync(timeout.Token));

                var (blockerTaskId, blockerAttemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    project.Id,
                    "Hold an active ancestor claim",
                    DateTimeOffset.UtcNow.AddMilliseconds(20),
                    timeout.Token);
                var (blockedTaskId, blockedAttemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    project.Id,
                    "Blocked by an active ancestor claim",
                    DateTimeOffset.UtcNow.AddMilliseconds(30),
                    timeout.Token);
                var blockingAcquire = await store.AcquireAsync(
                    new AttemptWorkspaceAcquireCommand
                    {
                        TenantId = tenantId,
                        ProjectId = project.Id,
                        TaskId = blockerTaskId,
                        AttemptId = blockerAttemptId,
                        RepositoryRoot = repository,
                        ControlledRoot = controlledRoot,
                        BaseReference = "HEAD",
                        BranchName = "task/hold-ancestor-claim",
                        WorktreePath = Path.Combine(controlledRoot, "worktrees", "blocker"),
                        ScopeClaims = ["src/failing/**"],
                        Owner = "orchestrator-4",
                        LeaseDuration = TimeSpan.FromMinutes(1),
                        IdempotencyKey = "orchestrate-blocker",
                        OccurredAt = DateTimeOffset.UtcNow,
                    },
                    timeout.Token);
                Assert.Equal(AttemptWorkspaceMutationStatus.Applied, blockingAcquire.Status);

                var conflicted = await orchestrator.ExecuteAsync(
                    CreateCommand(
                        tenantId,
                        project.Id,
                        blockedTaskId,
                        blockedAttemptId,
                        repository,
                        controlledRoot,
                        Path.Combine(controlledRoot, "worktrees", "blocked"),
                        "task/blocked-feature",
                        ["src/failing/parser.cs"],
                        "orchestrator-3",
                        "orchestrate-blocked"),
                    timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.ScopeConflict, conflicted.Status);
                Assert.Equal(
                    blockerAttemptId,
                    Assert.Single(conflicted.Conflicts).ExistingAttemptId);
                Assert.False(Directory.Exists(Path.Combine(controlledRoot, "worktrees", "blocked")));
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

    private static StartIsolatedExecutionCommand CreateCommand(
        string tenantId,
        string projectId,
        string taskId,
        string attemptId,
        string repository,
        string controlledRoot,
        string worktree,
        string branch,
        IReadOnlyList<string> claims,
        string owner,
        string idempotencyKey) =>
        new()
        {
            TenantId = tenantId,
            ProjectId = projectId,
            TaskId = taskId,
            AttemptId = attemptId,
            ConversationId = UlidValue.New(DateTimeOffset.UtcNow).ToString(),
            AgentId = "software-engineer",
            Instruction = "Implement the requested slice inside the isolated worktree.",
            StatusDigestJson = """{"phase":"execution"}""",
            RepositoryRoot = repository,
            ControlledRoot = controlledRoot,
            BaseReference = "HEAD",
            BranchName = branch,
            WorktreePath = worktree,
            ScopeClaims = claims,
            Owner = owner,
            LeaseDuration = TimeSpan.FromMinutes(1),
            IdempotencyKey = idempotencyKey,
        };

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
            new CreateTaskRequest(projectId, title, "Implement inside the isolated worktree."),
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
            $"orchestration-test:{attemptId}",
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
            throw new NotSupportedException("The orchestration test uses process sessions only.");

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

    private sealed class FakeSandboxExecutorFactory(Func<IAgentExecutor> create)
        : ISandboxAgentExecutorFactory
    {
        public IAgentExecutor Create(SandboxProcessPlan plan, StartIsolatedExecutionCommand command) =>
            command.Instruction == "boom"
                ? new ThrowingAgentExecutor()
                : create();
    }

    private sealed class ThrowingAgentExecutor : IAgentExecutor
    {
        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new ThrowingAgentException("The isolated executor crashed mid-turn.");
    }

    private sealed class ThrowingAgentException(string message) : Exception(message);
}
