using System.Diagnostics;
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
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Git;

public sealed class IsolatedExecutionEndpointTests
{
    private static readonly string[] DefaultClaims = ["src/**"];
    private static readonly string[] ApiClaims = ["src/api/**"];

    [Fact]
    public async Task ExposesIsolatedExecutionThroughTypedApiWithConfiguredModeAndGuards()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"isolated-endpoint-{Guid.NewGuid():N}");
        var controlledRoot = Path.Combine(root, "controlled");
        var repository = Path.Combine(controlledRoot, "external-repository");
        var database = Path.Combine(root, "endpoint.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(controlledRoot);
        await CreateFixtureRepositoryAsync(repository, timeout.Token);

        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls",
                "http://127.0.0.1:0",
                "--Harness:DatabasePath",
                database,
                "--Harness:IsolatedExecution:Mode",
                "fake",
                "--Harness:IsolatedExecution:ControlledRoot",
                controlledRoot,
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var profile = await CreateProfileAsync(client, timeout.Token);
                var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                    .GetAsync(profile.Id, timeout.Token))!.TenantId;
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(
                    client,
                    organization.Id,
                    repository,
                    timeout.Token);
                var (_, attemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    project.Id,
                    "Deliver through the typed API",
                    DateTimeOffset.UtcNow,
                    timeout.Token);

                using var missingAttempt = await client.PostAsJsonAsync(
                    $"/api/v1/attempts/{UlidValue.New(DateTimeOffset.UtcNow)}/isolated-executions",
                    new { instruction = "Run it.", scopeClaims = DefaultClaims },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, missingAttempt.StatusCode);

                using var started = await client.PostAsJsonAsync(
                    $"/api/v1/attempts/{attemptId}/isolated-executions",
                    new { instruction = "Deliver the API slice.", scopeClaims = ApiClaims },
                    timeout.Token);
                started.EnsureSuccessStatusCode();
                var result = (await started.Content
                    .ReadFromJsonAsync<IsolatedExecutionResponse>(timeout.Token))!;
                Assert.Equal("completed", result.Status);
                Assert.Equal("fake", result.Execution!.Executor);
                Assert.Equal("completed", result.Workspace!.State);
                Assert.Equal("completed", result.Workspace.CleanupState);
                Assert.Equal($"task/attempt-{attemptId.ToLowerInvariant()}", result.Workspace.BranchName);
                Assert.NotNull(result.Workspace.ReleasedAt);
                Assert.False(Directory.Exists(result.Workspace.WorktreePath));
                var manager = await GitWorktreeManager.OpenAsync(
                    repository,
                    controlledRoot,
                    timeout.Token);
                Assert.Contains(
                    result.Workspace.BranchName,
                    await manager.ListLocalBranchesAsync(timeout.Token));

                using var replay = await client.PostAsJsonAsync(
                    $"/api/v1/attempts/{attemptId}/isolated-executions",
                    new { instruction = "Deliver the API slice.", scopeClaims = ApiClaims },
                    timeout.Token);
                replay.EnsureSuccessStatusCode();
                var replayed = (await replay.Content
                    .ReadFromJsonAsync<IsolatedExecutionResponse>(timeout.Token))!;
                Assert.Equal("completed", replayed.Status);
                Assert.Null(replayed.Execution);

                var withoutRepository = await CreateProjectAsync(
                    client,
                    organization.Id,
                    repositoryUrl: null,
                    timeout.Token,
                    key: "NOREPO");
                var (_, orphanAttemptId) = await CreateAttemptAsync(
                    app.Services,
                    client,
                    tenantId,
                    withoutRepository.Id,
                    "Attempt without repository",
                    DateTimeOffset.UtcNow.AddMilliseconds(10),
                    timeout.Token);
                using var missingRepository = await client.PostAsJsonAsync(
                    $"/api/v1/attempts/{orphanAttemptId}/isolated-executions",
                    new { instruction = "Run it.", scopeClaims = DefaultClaims },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Conflict, missingRepository.StatusCode);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }

            var disabledDatabase = Path.Combine(root, "disabled.db");
            var disabledCookies = new CookieContainer();
            await using var disabledApp = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", disabledDatabase]);
            await disabledApp.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = disabledCookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(disabledApp.Services) };
                await CreateProfileAsync(client, timeout.Token);
                using var disabled = await client.PostAsJsonAsync(
                    $"/api/v1/attempts/{UlidValue.New(DateTimeOffset.UtcNow)}/isolated-executions",
                    new { instruction = "Run it.", scopeClaims = DefaultClaims },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            }
            finally
            {
                await disabledApp.StopAsync(timeout.Token);
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
            new CreateTaskRequest(projectId, title, "Deliver through the endpoint."),
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
            $"endpoint-test:{attemptId}",
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
        string? repositoryUrl,
        CancellationToken token,
        string key = "POSEIDON")
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = $"Poseidon {key}",
                Key = key,
                Description = "Backend",
                RepositoryUrl = repositoryUrl,
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
