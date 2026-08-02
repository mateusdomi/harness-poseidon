using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Git;

public sealed class AttemptWorkspacePersistenceTests
{
    [Fact]
    public async Task DurableClaimsSurviveRestartEnforceLeasesAndReleaseAfterTerminalState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"attempt-workspaces-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "workspaces.db");
        var repository = Path.Combine(root, "managed-repository");
        var controlledRoot = Path.Combine(root, "worktrees");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(controlledRoot);
        string tenantId;
        string projectId;
        string firstTaskId;
        string firstAttemptId;
        string secondTaskId;
        string secondAttemptId;
        var baseInstant = DateTimeOffset.UtcNow;

        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                    var profile = await CreateProfileAsync(client, timeout.Token);
                    tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                        .GetAsync(profile.Id, timeout.Token))!.TenantId;
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                    projectId = project.Id;
                    (firstTaskId, firstAttemptId) = await CreateAttemptAsync(
                        app.Services,
                        client,
                        tenantId,
                        projectId,
                        "First isolated task",
                        baseInstant,
                        timeout.Token);
                    (secondTaskId, secondAttemptId) = await CreateAttemptAsync(
                        app.Services,
                        client,
                        tenantId,
                        projectId,
                        "Second isolated task",
                        baseInstant.AddMilliseconds(10),
                        timeout.Token);
                    var store = app.Services.GetRequiredService<IAttemptWorkspaceStore>();

                    var acquireCommand = CreateAcquire(
                        tenantId,
                        projectId,
                        firstTaskId,
                        firstAttemptId,
                        repository,
                        controlledRoot,
                        Path.Combine(controlledRoot, "first"),
                        "task/first",
                        ["src/api/**"],
                        "runner-1",
                        TimeSpan.FromMinutes(1),
                        "acquire-first",
                        baseInstant.AddSeconds(1));
                    var acquired = await store.AcquireAsync(acquireCommand, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, acquired.Status);
                    Assert.Equal(AttemptWorkspaceState.Claimed, acquired.Workspace!.State);
                    Assert.Equal(AttemptWorkspaceCleanupState.NotRequired, acquired.Workspace.CleanupState);
                    Assert.Equal(1, acquired.Workspace.FencingToken);
                    Assert.All(
                        acquired.Workspace.ScopeClaims,
                        claim => Assert.True(UlidValue.TryParse(claim.ClaimId, out _)));
                    Assert.Equal(
                        firstAttemptId,
                        Assert.Single(await store.ListActiveAsync(tenantId, timeout.Token)).AttemptId);

                    var replay = await store.AcquireAsync(acquireCommand, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.IdempotentReplay, replay.Status);
                    Assert.Equal(1, replay.Workspace!.Version);

                    await Assert.ThrowsAsync<IdempotencyConflictException>(() => store.AcquireAsync(
                        acquireCommand with { BranchName = "task/first-other" },
                        timeout.Token));
                }
                finally
                {
                    await app.StopAsync(timeout.Token);
                }
            }

            await using (var restarted = CreateHost(database))
            {
                await restarted.StartAsync(timeout.Token);
                try
                {
                    var store = restarted.Services.GetRequiredService<IAttemptWorkspaceStore>();

                    var conflict = await store.AcquireAsync(CreateAcquire(
                        tenantId,
                        projectId,
                        secondTaskId,
                        secondAttemptId,
                        repository,
                        controlledRoot,
                        Path.Combine(controlledRoot, "second"),
                        "task/second",
                        ["src/api/controller.cs"],
                        "runner-2",
                        TimeSpan.FromMinutes(1),
                        "acquire-second",
                        baseInstant.AddSeconds(2)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.ScopeConflict, conflict.Status);
                    Assert.Null(conflict.Workspace);
                    Assert.Equal(firstAttemptId, Assert.Single(conflict.Conflicts).ExistingAttemptId);

                    var heartbeat = await store.HeartbeatAsync(new AttemptWorkspaceLeaseCommand(
                        tenantId,
                        firstAttemptId,
                        "runner-1",
                        1,
                        TimeSpan.FromMinutes(1),
                        baseInstant.AddSeconds(30)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, heartbeat.Status);
                    Assert.Equal(baseInstant.AddSeconds(30), heartbeat.Workspace!.LastHeartbeatAt);
                    Assert.Equal(baseInstant.AddSeconds(90), heartbeat.Workspace.LeaseExpiresAt);

                    var premature = await store.ReclaimExpiredAsync(new AttemptWorkspaceReclaimCommand(
                        tenantId,
                        firstAttemptId,
                        "runner-3",
                        TimeSpan.FromMinutes(1),
                        baseInstant.AddSeconds(40)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.LeaseRejected, premature.Status);

                    var reclaimed = await store.ReclaimExpiredAsync(new AttemptWorkspaceReclaimCommand(
                        tenantId,
                        firstAttemptId,
                        "runner-3",
                        TimeSpan.FromMinutes(5),
                        baseInstant.AddSeconds(120)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, reclaimed.Status);
                    Assert.Equal("runner-3", reclaimed.Workspace!.Owner);
                    Assert.Equal(2, reclaimed.Workspace.FencingToken);

                    var staleHeartbeat = await store.HeartbeatAsync(new AttemptWorkspaceLeaseCommand(
                        tenantId,
                        firstAttemptId,
                        "runner-1",
                        1,
                        TimeSpan.FromMinutes(1),
                        baseInstant.AddSeconds(121)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.LeaseRejected, staleHeartbeat.Status);

                    var staleTransition = await store.TransitionAsync(new AttemptWorkspaceTransitionCommand
                    {
                        TenantId = tenantId,
                        AttemptId = firstAttemptId,
                        Owner = "runner-1",
                        FencingToken = 1,
                        ExpectedState = AttemptWorkspaceState.Claimed,
                        State = AttemptWorkspaceState.Failed,
                        FinalError = "The stale owner must not fail the workspace.",
                        OccurredAt = baseInstant.AddSeconds(122),
                    }, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.LeaseRejected, staleTransition.Status);

                    var failed = await store.TransitionAsync(new AttemptWorkspaceTransitionCommand
                    {
                        TenantId = tenantId,
                        AttemptId = firstAttemptId,
                        Owner = "runner-3",
                        FencingToken = 2,
                        ExpectedState = AttemptWorkspaceState.Claimed,
                        State = AttemptWorkspaceState.Failed,
                        FinalError = "Sandbox bootstrap failed before the branch was created.",
                        OccurredAt = baseInstant.AddSeconds(123),
                    }, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, failed.Status);
                    Assert.Equal(AttemptWorkspaceState.Failed, failed.Workspace!.State);
                    Assert.Equal(AttemptWorkspaceCleanupState.Pending, failed.Workspace.CleanupState);
                    Assert.Equal(
                        "Sandbox bootstrap failed before the branch was created.",
                        failed.Workspace.FinalError);

                    var released = await store.ReleaseAsync(new AttemptWorkspaceReleaseCommand(
                        tenantId,
                        firstAttemptId,
                        "runner-3",
                        2,
                        baseInstant.AddSeconds(124)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, released.Status);
                    Assert.Equal(AttemptWorkspaceState.Failed, released.Workspace!.State);
                    Assert.Equal(AttemptWorkspaceCleanupState.Completed, released.Workspace.CleanupState);
                    Assert.NotNull(released.Workspace.ReleasedAt);
                    Assert.All(released.Workspace.ScopeClaims, claim => Assert.NotNull(claim.ReleasedAt));

                    var releaseReplay = await store.ReleaseAsync(new AttemptWorkspaceReleaseCommand(
                        tenantId,
                        firstAttemptId,
                        "runner-3",
                        2,
                        baseInstant.AddSeconds(125)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.IdempotentReplay, releaseReplay.Status);

                    var acquired = await store.AcquireAsync(CreateAcquire(
                        tenantId,
                        projectId,
                        secondTaskId,
                        secondAttemptId,
                        repository,
                        controlledRoot,
                        Path.Combine(controlledRoot, "second"),
                        "task/second",
                        ["src/api/controller.cs"],
                        "runner-2",
                        TimeSpan.FromMinutes(5),
                        "acquire-second",
                        baseInstant.AddSeconds(126)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, acquired.Status);

                    var technicalExecutionId = UlidValue.New(baseInstant.AddSeconds(127)).ToString();
                    var prepared = await store.TransitionAsync(new AttemptWorkspaceTransitionCommand
                    {
                        TenantId = tenantId,
                        AttemptId = secondAttemptId,
                        Owner = "runner-2",
                        FencingToken = 1,
                        ExpectedState = AttemptWorkspaceState.Claimed,
                        State = AttemptWorkspaceState.Prepared,
                        CommitSha = new string('a', 40),
                        OccurredAt = baseInstant.AddSeconds(127),
                    }, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, prepared.Status);
                    Assert.Equal(2, prepared.Workspace!.Version);

                    var running = await store.TransitionAsync(new AttemptWorkspaceTransitionCommand
                    {
                        TenantId = tenantId,
                        AttemptId = secondAttemptId,
                        Owner = "runner-2",
                        FencingToken = 1,
                        ExpectedState = AttemptWorkspaceState.Prepared,
                        State = AttemptWorkspaceState.Running,
                        SessionId = "thr_workspace",
                        TechnicalExecutionId = technicalExecutionId,
                        OccurredAt = baseInstant.AddSeconds(128),
                    }, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, running.Status);
                    Assert.Equal("thr_workspace", running.Workspace!.SessionId);
                    Assert.Equal(technicalExecutionId, running.Workspace.TechnicalExecutionId);

                    var completed = await store.TransitionAsync(new AttemptWorkspaceTransitionCommand
                    {
                        TenantId = tenantId,
                        AttemptId = secondAttemptId,
                        Owner = "runner-2",
                        FencingToken = 1,
                        ExpectedState = AttemptWorkspaceState.Running,
                        State = AttemptWorkspaceState.Completed,
                        OccurredAt = baseInstant.AddSeconds(129),
                    }, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, completed.Status);
                    Assert.Equal(new string('a', 40), completed.Workspace!.CommitSha);
                    Assert.Equal(AttemptWorkspaceCleanupState.Pending, completed.Workspace.CleanupState);

                    var transitionReplay = await store.TransitionAsync(new AttemptWorkspaceTransitionCommand
                    {
                        TenantId = tenantId,
                        AttemptId = secondAttemptId,
                        Owner = "runner-2",
                        FencingToken = 1,
                        ExpectedState = AttemptWorkspaceState.Running,
                        State = AttemptWorkspaceState.Completed,
                        OccurredAt = baseInstant.AddSeconds(130),
                    }, timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.IdempotentReplay, transitionReplay.Status);

                    var cleaned = await store.ReleaseAsync(new AttemptWorkspaceReleaseCommand(
                        tenantId,
                        secondAttemptId,
                        "runner-2",
                        1,
                        baseInstant.AddSeconds(131)), timeout.Token);
                    Assert.Equal(AttemptWorkspaceMutationStatus.Applied, cleaned.Status);
                    Assert.Equal(AttemptWorkspaceCleanupState.Completed, cleaned.Workspace!.CleanupState);
                    Assert.Equal(5, cleaned.Workspace.Version);

                    Assert.Empty(await store.ListExpiredAsync(
                        tenantId,
                        baseInstant.AddSeconds(200),
                        timeout.Token));
                    Assert.Empty(await store.ListActiveAsync(tenantId, timeout.Token));

                    var migrationCount = await SqliteMigrationRunner.ApplyAsync(
                        restarted.Services.GetRequiredService<SqliteWriteDispatcher>(),
                        timeout.Token);
                    Assert.Equal(0, migrationCount);
                }
                finally
                {
                    await restarted.StopAsync(timeout.Token);
                }
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

    private static AttemptWorkspaceAcquireCommand CreateAcquire(
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
        TimeSpan leaseDuration,
        string idempotencyKey,
        DateTimeOffset at) =>
        new()
        {
            TenantId = tenantId,
            ProjectId = projectId,
            TaskId = taskId,
            AttemptId = attemptId,
            RepositoryRoot = repository,
            ControlledRoot = controlledRoot,
            BaseReference = "HEAD",
            BranchName = branch,
            WorktreePath = worktree,
            ScopeClaims = claims,
            Owner = owner,
            LeaseDuration = leaseDuration,
            IdempotencyKey = idempotencyKey,
            OccurredAt = at,
        };

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
            $"workspace-test:{attemptId}",
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
}
