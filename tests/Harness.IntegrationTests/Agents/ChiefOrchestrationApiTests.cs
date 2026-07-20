using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Providers;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

public sealed class ChiefOrchestrationApiTests
{
    [Fact]
    public async Task ChiefCommandsFenceHandoffAndDrainBusinessAndDurableWorkAcrossRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"chief-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "chief.db");
        Directory.CreateDirectory(root);
        var cookies = new CookieContainer();
        string profileId;
        string projectId;
        string oldChiefId;
        string newChiefId;
        string taskId;
        string attemptId;
        string executionId;

        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                    var profile = await CreateProfileAsync(client, timeout.Token); profileId = profile.Id;
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                    projectId = project.Id; oldChiefId = project.ChiefAgentId;

                    using var taskResponse = await client.PostAsJsonAsync("/api/v1/tasks",
                        new CreateTaskRequest(projectId, "Drain me", "Run under the durable authority"), timeout.Token);
                    taskResponse.EnsureSuccessStatusCode();
                    var task = (await taskResponse.Content.ReadFromJsonAsync<BoardTaskContract>(timeout.Token))!;
                    taskId = task.Id;

                    var profileStore = app.Services.GetRequiredService<ILocalProfileStore>();
                    var localProfile = (await profileStore.GetAsync(profileId, timeout.Token))!;
                    var board = app.Services.GetRequiredService<IWorkBoardStore>();
                    var chain = app.Services.GetRequiredService<IWorkChainStore>();
                    var persistedTask = (await board.GetTaskAsync(localProfile.TenantId, taskId, timeout.Token))!;
                    var instruction = Assert.Single(await board.ListInstructionsAsync(localProfile.TenantId, taskId, null, 10, timeout.Token));
                    var now = DateTimeOffset.UtcNow;
                    attemptId = UlidValue.New(now).ToString();
                    var started = await chain.StartAttemptAsync(new(localProfile.TenantId,
                        persistedTask.BackingSolicitationId, taskId, instruction.Id, attemptId,
                        oldChiefId, persistedTask.Version, $"chief-test:{attemptId}", now), timeout.Token);
                    Assert.Equal(WorkChainMutationStatus.Applied, started.Status);

                    executionId = UlidValue.New(now.AddMilliseconds(1)).ToString();
                    var engine = app.Services.GetRequiredService<IDurableExecutionEngine>();
                    var durableStarted = await engine.StartAsync(new(localProfile.TenantId, projectId,
                        executionId, "{\"kind\":\"chief-test\"}",
                        new DurableRetryPolicy(2, TimeSpan.Zero, 1m, TimeSpan.Zero), now,
                        $"chief-test:durable:{executionId}"), now, timeout.Token);
                    Assert.Equal(DurableCommandStatus.Applied, durableStarted.Status);
                    Assert.NotNull(await engine.TryAcquireNextAsync(localProfile.TenantId, "chief-test-owner",
                        now.AddMilliseconds(2), TimeSpan.FromMinutes(1), timeout.Token));

                    using (var pause = await client.PostAsync($"/api/v1/projects/{projectId}/chief/pause", null, timeout.Token))
                    {
                        var paused = await pause.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                        Assert.Equal(HttpStatusCode.OK, pause.StatusCode); Assert.Equal("paused", paused?.State);
                    }
                    Assert.Equal("waiting", (await client.GetFromJsonAsync<AgentContract>($"/api/v1/agents/{oldChiefId}", timeout.Token))?.State);
                    using (var resume = await client.PostAsync($"/api/v1/projects/{projectId}/chief/resume", null, timeout.Token))
                    {
                        var resumed = await resume.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                        Assert.Equal(HttpStatusCode.OK, resume.StatusCode); Assert.Equal("active", resumed?.State);
                    }

                    var definitions = (await client.GetFromJsonAsync<AgentDefinitionPage>("/api/v1/agent-definitions?limit=10", timeout.Token))!;
                    var specialist = definitions.Items.Single(x => x.Name == "Software Engineer");
                    string customDefinitionId;
                    var custom = new AgentDefinitionWriteRequest(
                        "security-reviewer", "Security Reviewer", "specialist", "Application security",
                        "Reviews threats and evidence.", specialist.DefaultModelId, specialist.SkillIds,
                        specialist.ToolIds, "Skeptical reviewer", "Prevent exploitable releases.",
                        ["Verify evidence"], ["Threat report"], ["No unresolved critical risk"],
                        "Direct and traceable", ["Cannot approve own work"],
                        ["dotnet", "security"], "high", null, [], "Platform", "critic", "high");
                    using (var createDefinition = await client.PostAsJsonAsync("/api/v1/agent-definitions", custom, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, createDefinition.StatusCode);
                        var created = (await createDefinition.Content.ReadFromJsonAsync<AgentDefinitionContract>(timeout.Token))!;
                        customDefinitionId = created.Id; Assert.Equal(1, created.Version); Assert.True(created.Enabled);
                        Assert.Equal(["dotnet", "security"], created.Stacks);
                        Assert.Equal(("high", "Platform", "critic", "high"),
                            (created.DefaultEffort, created.Team, created.ActorCritic, created.Risk));
                    }
                    using (var updateDefinition = await client.PatchAsJsonAsync($"/api/v1/agent-definitions/{customDefinitionId}", custom with { Name = "Senior Security Reviewer", ExpectedVersion = 1 }, timeout.Token))
                    { updateDefinition.EnsureSuccessStatusCode(); Assert.Equal(2, (await updateDefinition.Content.ReadFromJsonAsync<AgentDefinitionContract>(timeout.Token))?.Version); }
                    var versions = (await client.GetFromJsonAsync<AgentDefinitionVersionPage>(
                        $"/api/v1/agent-definitions/{customDefinitionId}/versions?limit=1", timeout.Token))!;
                    var latestVersion = Assert.Single(versions.Items);
                    Assert.Equal(2, latestVersion.Version);
                    Assert.Equal("Senior Security Reviewer", latestVersion.Snapshot.Name);
                    Assert.Equal(profileId, latestVersion.ActorProfileId);
                    Assert.Equal(2, versions.NextBeforeVersion);
                    var initialVersions = (await client.GetFromJsonAsync<AgentDefinitionVersionPage>(
                        $"/api/v1/agent-definitions/{customDefinitionId}/versions?beforeVersion=2&limit=1",
                        timeout.Token))!;
                    Assert.Equal("Security Reviewer", Assert.Single(initialVersions.Items).Snapshot.Name);
                    string duplicateId;
                    using (var duplicateDefinition = await client.PostAsJsonAsync($"/api/v1/agent-definitions/{customDefinitionId}/duplicate", new AgentDefinitionDuplicateRequest("security-reviewer-copy", "Security Reviewer Copy"), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, duplicateDefinition.StatusCode); duplicateId = (await duplicateDefinition.Content.ReadFromJsonAsync<AgentDefinitionContract>(timeout.Token))!.Id; }
                    using (var deleteDefinition = await client.DeleteAsync($"/api/v1/agent-definitions/{duplicateId}", timeout.Token)) Assert.Equal(HttpStatusCode.NoContent, deleteDefinition.StatusCode);
                    using (var disableDefinition = await client.PostAsync($"/api/v1/agent-definitions/{customDefinitionId}/disable", null, timeout.Token)) { disableDefinition.EnsureSuccessStatusCode(); Assert.False((await disableDefinition.Content.ReadFromJsonAsync<AgentDefinitionContract>(timeout.Token))!.Enabled); }
                    using (var enableDefinition = await client.PostAsync($"/api/v1/agent-definitions/{customDefinitionId}/enable", null, timeout.Token)) { enableDefinition.EnsureSuccessStatusCode(); Assert.True((await enableDefinition.Content.ReadFromJsonAsync<AgentDefinitionContract>(timeout.Token))!.Enabled); }
                    using (var archiveDefinition = await client.PostAsync($"/api/v1/agent-definitions/{customDefinitionId}/archive", null, timeout.Token)) { archiveDefinition.EnsureSuccessStatusCode(); Assert.NotNull((await archiveDefinition.Content.ReadFromJsonAsync<AgentDefinitionContract>(timeout.Token))!.ArchivedAt); }
                    Assert.DoesNotContain(customDefinitionId, (await client.GetFromJsonAsync<AgentDefinitionPage>("/api/v1/agent-definitions?limit=20", timeout.Token))!.Items.Select(value => value.Id));
                    Assert.Contains(customDefinitionId, (await client.GetFromJsonAsync<AgentDefinitionPage>("/api/v1/agent-definitions?limit=20&includeArchived=true", timeout.Token))!.Items.Select(value => value.Id));
                    using (var invalid = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/chief/handoff",
                        new HandoffChiefRequest(specialist.Id, null, "Specialist cannot own the Chief lease."), timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);

                    using (var handoff = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/chief/handoff",
                        new HandoffChiefRequest(null, null, "Rotate the orchestration lease."), timeout.Token))
                    {
                        handoff.EnsureSuccessStatusCode();
                        var next = (await handoff.Content.ReadFromJsonAsync<AgentContract>(timeout.Token))!;
                        newChiefId = next.Id; Assert.Equal(2, next.Lease?.FencingToken); Assert.Equal(projectId, next.ProjectId);
                    }
                    Assert.Null((await client.GetFromJsonAsync<AgentContract>($"/api/v1/agents/{oldChiefId}", timeout.Token))?.Lease);
                    var orgChart = await client.GetFromJsonAsync<AgentOrgChartContract>(
                        $"/api/v1/projects/{projectId}/agent-org-chart", timeout.Token);
                    Assert.Equal(newChiefId, orgChart?.RootAgentId);
                    Assert.Equal([newChiefId, oldChiefId], orgChart?.Nodes.Select(node => node.AgentId));
                    Assert.Null(orgChart?.Nodes[0].ParentAgentId);
                    Assert.Equal(newChiefId, orgChart?.Nodes[1].ParentAgentId);
                    Assert.Equal([0, 1], orgChart?.Nodes.Select(node => node.Level));

                    var accounts = (await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts", timeout.Token))!;
                    var models = (await client.GetFromJsonAsync<ModelPage>("/api/v1/models", timeout.Token))!;
                    var account = accounts.Items.Single(value => value.State == "active");
                    var compatible = models.Items.Where(value => value.ProviderId == account.ProviderId && value.Enabled).ToArray();
                    using (var selection = await client.PatchAsJsonAsync($"/api/v1/agents/{newChiefId}/selection",
                        new AgentSelectionRequest(account.Id, compatible[0].Id, "max", [compatible[1].Id],
                            "Prefer the strongest mapped effort with a compatible fallback."), timeout.Token))
                    {
                        selection.EnsureSuccessStatusCode(); var selected = (await selection.Content.ReadFromJsonAsync<AgentContract>(timeout.Token))!;
                        Assert.Equal(account.Id, selected.AccountId); Assert.Equal("max", selected.Effort);
                        Assert.Equal("xhigh", selected.ProviderEffortValue); Assert.Equal([compatible[1].Id], selected.FallbackModelIds);
                    }

                    await app.Services.GetRequiredService<SqliteWriteDispatcher>().ExecuteAsync(async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = "UPDATE agents SET state='working',current_task_id=$task WHERE id=$agent;";
                        command.Parameters.AddWithValue("$task", taskId); command.Parameters.AddWithValue("$agent", newChiefId);
                        await command.ExecuteNonQueryAsync(token);
                    }, timeout.Token);

                    using (var drain = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/chief/drain",
                        new DrainChiefRequest("Operator requested a safe drain."), timeout.Token))
                    {
                        drain.EnsureSuccessStatusCode(); Assert.Equal(1, await drain.Content.ReadFromJsonAsync<int>(timeout.Token));
                    }
                    Assert.Equal("ready", (await client.GetFromJsonAsync<BoardTaskContract>($"/api/v1/tasks/{taskId}", timeout.Token))?.State);
                    Assert.Equal("cancelled", (await client.GetFromJsonAsync<AttemptContract>($"/api/v1/attempts/{attemptId}", timeout.Token))?.State);
                    Assert.Equal("idle", (await client.GetFromJsonAsync<AgentContract>($"/api/v1/agents/{newChiefId}", timeout.Token))?.State);
                    Assert.Equal(DurableExecutionState.Cancelled, (await engine.GetAsync(localProfile.TenantId, executionId, timeout.Token))?.State);

                    var projectEvents = await WaitForEventAsync(client, $"project:{projectId}", "task.stateChanged", timeout.Token);
                    var drained = projectEvents.Delta.Last(x => x.Type == "task.stateChanged").Payload;
                    Assert.Equal("chief", drained.GetProperty("changedByKind").GetString());
                    var globalEvents = await WaitForAuditActionAsync(client, "chief.tasksDrained", timeout.Token);
                    Assert.Contains(globalEvents.Delta, x => x.Type == "agent.statusChanged");
                    Assert.Contains(globalEvents.Delta, x => x.Type == "audit.eventAppended" &&
                        x.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == "chief.tasksDrained");
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                var project = await client.GetFromJsonAsync<ProjectResponse>($"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal(newChiefId, project?.ChiefAgentId);
                Assert.Equal("cancelled", (await client.GetFromJsonAsync<AttemptContract>($"/api/v1/attempts/{attemptId}", timeout.Token))?.State);
                Assert.Equal("ready", (await client.GetFromJsonAsync<BoardTaskContract>($"/api/v1/tasks/{taskId}", timeout.Token))?.State);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<EventStreamSnapshot> WaitForEventAsync(HttpClient client, string stream, string type, CancellationToken token)
    {
        for (var i = 0; i < 200; i++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                $"/api/v1/event-streams/snapshot?stream={Uri.EscapeDataString(stream)}", token);
            if (snapshot is not null && snapshot.Delta.Any(x => x.Type == type)) return snapshot;
            await Task.Delay(25, token);
        }
        throw new TimeoutException($"Event {type} was not dispatched to {stream}.");
    }

    private static async Task<EventStreamSnapshot> WaitForAuditActionAsync(
        HttpClient client,
        string action,
        CancellationToken token)
    {
        for (var i = 0; i < 200; i++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                "/api/v1/event-streams/snapshot?stream=global",
                token);
            if (snapshot is not null && snapshot.Delta.Any(x =>
                    x.Type == "audit.eventAppended" &&
                    x.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == action))
            {
                return snapshot;
            }

            await Task.Delay(25, token);
        }

        throw new TimeoutException($"Audit action {action} was not dispatched to global.");
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
