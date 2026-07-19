using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Execution;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Modules.Execution.Infrastructure.Sandbox;
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

public sealed class DockerIsolatedAttemptCompositionTests
{
    [Fact]
    public async Task OrchestratesExternalAttemptThroughRealDockerSandboxAndCodexProtocol()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "dogfood-1e",
            $"{Guid.NewGuid():N}"[..12]);
        var controlledRoot = Path.Combine(root, "controlled");
        var externalRepository = Path.Combine(controlledRoot, "external-repository");
        var database = Path.Combine(root, "composition.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(controlledRoot);
        await CreateFixtureRepositoryAsync(externalRepository, timeout.Token);
        var provider = new DockerSandboxProvider();
        string? sandboxLabel = null;

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
                var (taskId, attemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    project.Id,
                    "Deliver through the real docker sandbox",
                    DateTimeOffset.UtcNow,
                    timeout.Token);
                sandboxLabel = IsolatedAttemptOrchestrator.SandboxAttemptLabel(attemptId);
                var imageName = $"harness-composition:{sandboxLabel}";
                await provider.BuildImageAsync(
                    sandboxLabel,
                    imageName,
                    Path.Combine(repositoryRoot, "infra", "sandbox", "poc6"),
                    timeout.Token);

                var options = new IsolatedExecutionOptions
                {
                    AgentImageName = imageName,
                    ProxyImageName = imageName,
                    ProxyCommand = "proxy",
                    ContainerExecutable = "fake-codex",
                    CpuLimit = 0.5m,
                    MemoryBytes = 64 * 1024 * 1024,
                    WritableDiskBytes = 8 * 1024 * 1024,
                    PidsLimit = 64,
                    HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                };
                var store = app.Services.GetRequiredService<IAttemptWorkspaceStore>();
                var orchestrator = new IsolatedAttemptOrchestrator(
                    store,
                    provider,
                    new CodexCliSandboxExecutorFactory(options),
                    SystemClock.Instance,
                    options);
                var worktree = Path.Combine(controlledRoot, "worktrees", "dogfood");
                var command = new StartIsolatedExecutionCommand
                {
                    TenantId = tenantId,
                    ProjectId = project.Id,
                    TaskId = taskId,
                    AttemptId = attemptId,
                    ConversationId = UlidValue.New(DateTimeOffset.UtcNow).ToString(),
                    AgentId = "software-engineer",
                    Instruction = "Complete the isolated fixture.",
                    StatusDigestJson = """{"phase":"dogfood"}""",
                    RepositoryRoot = externalRepository,
                    ControlledRoot = controlledRoot,
                    BaseReference = "HEAD",
                    BranchName = "task/docker-composition",
                    WorktreePath = worktree,
                    ScopeClaims = ["src/**"],
                    Owner = "composer-1",
                    LeaseDuration = TimeSpan.FromMinutes(2),
                    IdempotencyKey = "docker-composition",
                };

                var completed = await orchestrator.ExecuteAsync(command, timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, completed.Status);
                Assert.Equal("codex-cli", completed.Execution!.Executor);
                Assert.Equal("thr_docker", completed.Execution.SessionId);
                Assert.Equal(AttemptWorkspaceState.Completed, completed.Workspace!.State);
                Assert.Equal("thr_docker", completed.Workspace.SessionId);
                Assert.Equal(40, completed.Workspace.CommitSha!.Length);

                // O fake-codex gravou trabalho não commitado: o cleanup preserva a worktree
                // suja e mantém o claim retido com cleanup pendente.
                Assert.Equal(
                    AttemptWorkspaceCleanupState.Pending,
                    completed.Workspace.CleanupState);
                Assert.True(File.Exists(Path.Combine(worktree, "docker-codex-ran.txt")));
                Assert.True((await provider.DetectResourcesAsync(sandboxLabel, timeout.Token)).IsEmpty);

                await RunGitAsync(worktree, ["add", "docker-codex-ran.txt"], timeout.Token);
                await RunGitAsync(
                    worktree,
                    ["commit", "-m", "feat: record docker codex run"],
                    timeout.Token);

                var reconciled = await orchestrator.ExecuteAsync(command, timeout.Token);
                Assert.Equal(IsolatedExecutionStatus.Completed, reconciled.Status);
                Assert.Null(reconciled.Execution);
                Assert.Equal(
                    AttemptWorkspaceCleanupState.Completed,
                    reconciled.Workspace!.CleanupState);
                Assert.NotNull(reconciled.Workspace.ReleasedAt);
                Assert.All(
                    reconciled.Workspace.ScopeClaims,
                    claim => Assert.NotNull(claim.ReleasedAt));
                Assert.False(Directory.Exists(worktree));
                var manager = await GitWorktreeManager.OpenAsync(
                    externalRepository,
                    controlledRoot,
                    timeout.Token);
                Assert.Contains(
                    "task/docker-composition",
                    await manager.ListLocalBranchesAsync(timeout.Token));
                Assert.Single(await manager.ListWorktreesAsync(timeout.Token));
                Assert.True((await provider.DetectResourcesAsync(sandboxLabel, timeout.Token)).IsEmpty);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (sandboxLabel is not null)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await provider.CleanupAsync(sandboxLabel, cleanupTimeout.Token);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

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
            new CreateTaskRequest(projectId, title, "Deliver inside the docker sandbox."),
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
            $"composition-test:{attemptId}",
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Repository root not found from {AppContext.BaseDirectory}."));
    }
}
