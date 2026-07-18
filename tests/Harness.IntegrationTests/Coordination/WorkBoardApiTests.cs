using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Coordination;

public sealed class WorkBoardApiTests
{
    [Fact]
    public async Task WorkBoardCreationProjectionEventsAndRestartMatchFrontendContract()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"board-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "board.db"); Directory.CreateDirectory(root);
        var cookies = new CookieContainer(); string profileId; string projectId; string taskId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.GetAsync("/api/v1/tasks", timeout.Token))
                        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    var profile = await CreateProfileAsync(client, timeout.Token); profileId = profile.Id;
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token); projectId = project.Id;

                    using var solicitationResponse = await client.PostAsJsonAsync("/api/v1/solicitations",
                        new CreateSolicitationRequest(projectId, "request", "Novo endpoint", "Precisamos do quadro real."), timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, solicitationResponse.StatusCode);
                    var solicitation = await solicitationResponse.Content.ReadFromJsonAsync<SolicitationContract>(timeout.Token);
                    Assert.NotNull(solicitation); Assert.Equal(profileId, solicitation.AuthorProfileId); Assert.Equal("open", solicitation.State);

                    using var demandResponse = await client.PostAsJsonAsync("/api/v1/demands",
                        new CreateDemandRequest(projectId, "Quadro", "Publicar recursos", solicitation.Id, "high"), timeout.Token);
                    var demand = await demandResponse.Content.ReadFromJsonAsync<DemandContract>(timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, demandResponse.StatusCode); Assert.Equal(solicitation.Id, demand?.SolicitationId);

                    using var independentDemandResponse = await client.PostAsJsonAsync("/api/v1/demands",
                        new CreateDemandRequest(projectId, "Conversa", "Demanda originada no chat"), timeout.Token);
                    var independentDemand = await independentDemandResponse.Content.ReadFromJsonAsync<DemandContract>(timeout.Token);
                    Assert.Null(independentDemand?.SolicitationId);

                    using var taskResponse = await client.PostAsJsonAsync("/api/v1/tasks",
                        new CreateTaskRequest(projectId, "Implementar quadro", "Implemente e valide", demand?.Id, "high"), timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, taskResponse.StatusCode);
                    var task = await taskResponse.Content.ReadFromJsonAsync<BoardTaskContract>(timeout.Token);
                    Assert.NotNull(task); taskId = task.Id; Assert.Equal("backlog", task.State);
                    Assert.Equal((0m, 0m, 0m), (task.Progress.Executed, task.Progress.Validated, task.Progress.Approved));

                    using var independentTaskResponse = await client.PostAsJsonAsync("/api/v1/tasks",
                        new CreateTaskRequest(projectId, "Tarefa avulsa", "Planeje com segurança"), timeout.Token);
                    var independentTask = await independentTaskResponse.Content.ReadFromJsonAsync<BoardTaskContract>(timeout.Token);
                    Assert.Null(independentTask?.DemandId);

                    var solicitations = await client.GetFromJsonAsync<SolicitationPage>(
                        $"/api/v1/solicitations?projectId={projectId}", timeout.Token);
                    Assert.Single(solicitations?.Items ?? []);
                    var demands = await client.GetFromJsonAsync<DemandPage>(
                        $"/api/v1/demands?projectId={projectId}", timeout.Token);
                    Assert.Equal(2, demands?.Items.Count);
                    var tasks = await client.GetFromJsonAsync<TaskPage>(
                        $"/api/v1/tasks?projectId={projectId}", timeout.Token);
                    Assert.Equal(2, tasks?.Items.Count);
                    var instructions = await client.GetFromJsonAsync<InstructionPage>(
                        $"/api/v1/task-instructions?taskId={taskId}", timeout.Token);
                    Assert.Single(instructions?.Items ?? []); Assert.Equal("chief", instructions?.Items[0].AuthorKind);
                    Assert.Empty((await client.GetFromJsonAsync<AttemptPage>(
                        $"/api/v1/attempts?taskId={taskId}", timeout.Token))?.Items ?? []);
                    Assert.Empty((await client.GetFromJsonAsync<AttemptEventPage>(
                        "/api/v1/attempt-events", timeout.Token))?.Items ?? []);

                    var digest = await client.GetFromJsonAsync<Harness.Modules.Projects.Contracts.ProjectStatusDigestContract>(
                        $"/api/v1/projects/{projectId}/status-digest", timeout.Token);
                    Assert.Equal(2, digest?.TaskCounts.Backlog);
                    var snapshot = await WaitForBoardEventsAsync(client, projectId, timeout.Token);
                    Assert.Equal(2, snapshot.Delta.Count(x => x.Type == "demand.created"));
                    Assert.Equal(2, snapshot.Delta.Count(x => x.Type == "task.created"));
                    var createdTask = snapshot.Delta.Last(x => x.Type == "task.created").Payload.GetProperty("task");
                    Assert.Equal("backlog", createdTask.GetProperty("state").GetString());
                    Assert.Equal(projectId, createdTask.GetProperty("projectId").GetString());
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                var recovered = await client.GetFromJsonAsync<BoardTaskContract>($"/api/v1/tasks/{taskId}", timeout.Token);
                Assert.Equal("backlog", recovered?.State); Assert.Equal(projectId, recovered?.ProjectId);
                var page = await client.GetFromJsonAsync<TaskPage>($"/api/v1/tasks?projectId={projectId}", timeout.Token);
                Assert.Equal(2, page?.Items.Count);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<EventStreamSnapshot> WaitForBoardEventsAsync(HttpClient client, string projectId, CancellationToken token)
    {
        for (var i = 0; i < 200; i++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                $"/api/v1/event-streams/snapshot?stream=project:{projectId}", token);
            if (snapshot is not null && snapshot.Delta.Count(x => x.Type == "task.created") >= 2) return snapshot;
            await Task.Delay(25, token);
        }
        throw new TimeoutException("Board events were not dispatched.");
    }
    private static async Task<ProfileResponse> CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }
    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }
    private static async Task<ProjectResponse> CreateProjectAsync(HttpClient client, string organizationId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest
        { OrganizationId = organizationId, Name = "Poseidon", Key = "POSEIDON", Description = "Backend" }, token);
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
    }
    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
