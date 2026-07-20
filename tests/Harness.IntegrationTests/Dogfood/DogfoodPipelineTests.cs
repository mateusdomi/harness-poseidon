using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Execution;
using Harness.Host.Governance;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Host.WorkBoard;
using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Dogfood;

public sealed class DogfoodPipelineTests
{
    private static readonly string[] DogfoodClaims = ["src/status/**"];

    [Fact]
    public async Task SolicitationFlowsThroughChiefTaskIsolatedExecutionCriticAndAudit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"dogfood-{Guid.NewGuid():N}");
        var controlledRoot = Path.Combine(root, "controlled");
        var repository = Path.Combine(controlledRoot, "external-repository");
        var database = Path.Combine(root, "dogfood.db");
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

                // 1) Solicitação humana pelo chat; o Chief propõe a demanda estruturada.
                using var created = await client.PostAsJsonAsync(
                    "/api/v1/conversations",
                    new CreateConversationRequest(project.Id, "Dogfood"),
                    timeout.Token);
                created.EnsureSuccessStatusCode();
                var conversationId =
                    (await created.Content.ReadFromJsonAsync<ConversationResponse>(timeout.Token))!.Id;
                using var turnResponse = await client.PostAsJsonAsync(
                    $"/api/v1/conversations/{conversationId}/turns",
                    new StartChatTurnRequest(
                        "Precisamos expor o status do serviço.\n" +
                        "DEMANDA: Implementar endpoint de status | Adicionar o endpoint /status ao serviço"),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
                var handle = (await turnResponse.Content
                    .ReadFromJsonAsync<ChatTurnHandle>(timeout.Token))!;
                await WaitForTurnAsync(client, conversationId, handle.TurnId, timeout.Token);
                var governance = app.Services.GetRequiredService<IGovernanceRuntimeStore>();
                var chiefReceipt = await governance.GetReceiptAsync(
                    tenantId, handle.TurnId, timeout.Token);
                Assert.NotNull(chiefReceipt);
                Assert.Equal(GovernanceReceiptState.Completed, chiefReceipt.State);
                Assert.NotEmpty(chiefReceipt.Documents);
                Assert.Equal("pass", chiefReceipt.GateResult);
                Assert.Contains(
                    await governance.ListMetricsAsync(tenantId, handle.TurnId, timeout.Token),
                    metric => metric.Kind == GovernanceMetricKind.EvaluatorVerdict);
                var demand = Assert.Single((await client.GetFromJsonAsync<DemandPage>(
                    $"/api/v1/demands?projectId={project.Id}", timeout.Token))!.Items);
                Assert.Equal("Implementar endpoint de status", demand.Title);

                // 2) Tarefa criada a partir da demanda materializada.
                using var taskResponse = await client.PostAsJsonAsync(
                    "/api/v1/tasks",
                    new CreateTaskRequest(
                        project.Id,
                        "Implementar endpoint de status",
                        "Implemente GET /status devolvendo healthy.",
                        demand.Id),
                    timeout.Token);
                taskResponse.EnsureSuccessStatusCode();
                var task = (await taskResponse.Content
                    .ReadFromJsonAsync<BoardTaskContract>(timeout.Token))!;

                // 3) Tentativa de negócio iniciada pelo engenheiro.
                var board = app.Services.GetRequiredService<IWorkBoardStore>();
                var chain = app.Services.GetRequiredService<IWorkChainStore>();
                var persisted = (await board.GetTaskAsync(tenantId, task.Id, timeout.Token))!;
                var instruction = Assert.Single(await board.ListInstructionsAsync(
                    tenantId, task.Id, null, 10, timeout.Token));
                var attemptId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
                var startReceipt = await chain.StartAttemptAsync(new(
                    tenantId,
                    persisted.BackingSolicitationId,
                    task.Id,
                    instruction.Id,
                    attemptId,
                    "software-engineer",
                    persisted.Version,
                    $"dogfood-start:{attemptId}",
                    DateTimeOffset.UtcNow), timeout.Token);
                Assert.Equal(WorkChainMutationStatus.Applied, startReceipt.Status);

                // 4) Execução isolada real: claim, branch/worktree, sandbox e executor.
                using var executionResponse = await client.PostAsJsonAsync(
                    $"/api/v1/attempts/{attemptId}/isolated-executions",
                    new
                    {
                        instruction = "Implemente GET /status devolvendo healthy.",
                        scopeClaims = DogfoodClaims,
                    },
                    timeout.Token);
                executionResponse.EnsureSuccessStatusCode();
                var execution = (await executionResponse.Content
                    .ReadFromJsonAsync<IsolatedExecutionResponse>(timeout.Token))!;
                Assert.Equal("completed", execution.Status);
                Assert.NotNull(execution.Workspace!.CommitSha);
                Assert.NotNull(execution.Workspace.ReleasedAt);
                var manager = await GitWorktreeManager.OpenAsync(
                    repository, controlledRoot, timeout.Token);
                Assert.Contains(
                    execution.Workspace.BranchName,
                    await manager.ListLocalBranchesAsync(timeout.Token));

                // 5) Evidência e conclusão da tentativa de negócio.
                var afterStart = (await board.GetTaskAsync(tenantId, task.Id, timeout.Token))!;
                var completeReceipt = await chain.CompleteAttemptAsync(new(
                    tenantId,
                    persisted.BackingSolicitationId,
                    task.Id,
                    attemptId,
                    afterStart.Version,
                    [new WorkEvidenceInput(
                        UlidValue.New(DateTimeOffset.UtcNow).ToString(),
                        $"workspace:{execution.Workspace.BranchName}@{execution.Workspace.CommitSha}")],
                    $"dogfood-complete:{attemptId}",
                    DateTimeOffset.UtcNow), timeout.Token);
                Assert.Equal(WorkChainMutationStatus.Applied, completeReceipt.Status);

                // 6) Critic independente aprova (actor-critic).
                var afterComplete = (await board.GetTaskAsync(tenantId, task.Id, timeout.Token))!;
                var reviewReceipt = await chain.ReviewAttemptAsync(new(
                    tenantId,
                    persisted.BackingSolicitationId,
                    task.Id,
                    attemptId,
                    UlidValue.New(DateTimeOffset.UtcNow).ToString(),
                    "critic-qa",
                    "approved",
                    "Endpoint entregue com evidência de workspace isolado.",
                    afterComplete.Version,
                    $"dogfood-review:{attemptId}",
                    DateTimeOffset.UtcNow), timeout.Token);
                Assert.Equal(WorkChainMutationStatus.Applied, reviewReceipt.Status);
                var done = (await board.GetTaskAsync(tenantId, task.Id, timeout.Token))!;
                Assert.Equal("done", done.State);

                // 7) Auditoria ponta a ponta da solicitação ao aceite.
                var audit = (await client.GetFromJsonAsync<AuditEventPage>(
                    "/api/v1/audit-events?limit=200", timeout.Token))!;
                var actions = audit.Items.Select(item => item.Action).ToHashSet(StringComparer.Ordinal);
                foreach (var expected in (string[])
                [
                    "chat.turnCompleted",
                    "demand.created",
                    "task.created",
                    "attempt.workspaceClaimed",
                    "attempt.workspaceCompleted",
                    "attempt.workspaceCleaned",
                ])
                {
                    Assert.Contains(expected, actions);
                }
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

    private static async Task WaitForTurnAsync(
        HttpClient client,
        string conversationId,
        string turnId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                $"/api/v1/event-streams/snapshot?stream=conversation:{conversationId}",
                cancellationToken);
            if (snapshot is not null && snapshot.Delta.Any(item =>
                    item.Type == "chat.turnCompleted" &&
                    item.Payload.GetProperty("turnId").GetString() == turnId))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException("Chief turn events were not dispatched.");
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
        string repositoryUrl,
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
