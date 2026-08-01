using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Harness.IntegrationTests.Support;

namespace Harness.IntegrationTests.Projects;

public sealed class ProjectApiTests
{
    [Fact]
    public async Task ProjectCrudPersistsVersionedConfigurationAndCreatedEventAcrossRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"project-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "project.db");
        Directory.CreateDirectory(artifactRoot);
        var cookies = new CookieContainer();
        string profileId;
        string projectId;

        try
        {
            await using (var app = CreateHost(databasePath))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = GetBaseAddress(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var unauthorized = await anonymous.GetAsync("/api/v1/projects", timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                    }

                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using var profileResponse = await client.PostAsJsonAsync(
                        "/api/v1/profiles",
                        new CreateProfileRequest("Mateus", null, null, "pt-BR"),
                        timeout.Token);
                    var profile = await profileResponse.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token);
                    Assert.NotNull(profile);
                    profileId = profile.Id;
                    await ProviderCatalogTestSeed.SeedForLocalProfileAsync(app.Services, timeout.Token);

                    using var organizationResponse = await client.PostAsJsonAsync(
                        "/api/v1/organizations",
                        new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
                        timeout.Token);
                    var organization = await organizationResponse.Content
                        .ReadFromJsonAsync<OrganizationResponse>(timeout.Token);
                    Assert.NotNull(organization);

                    using var missingOrganization = await client.PostAsJsonAsync(
                        "/api/v1/projects",
                        Request("01ARZ3NDEKTSV4RRFFQ69G5FAV", "MISSING"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.NotFound, missingOrganization.StatusCode);

                    using var createdResponse = await client.PostAsJsonAsync(
                        "/api/v1/projects",
                        Request(organization.Id, "POSEIDON"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
                    var created = await createdResponse.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                    Assert.NotNull(created);
                    projectId = created.Id;
                    var definitions = await client.GetFromJsonAsync<AgentDefinitionPage>(
                        "/api/v1/agent-definitions?limit=10", timeout.Token);
                    Assert.Equal(
                        [
                            // Ordenadas por id: as 6 personas de sistema, depois as de Delivery (DEL-08).
                            "Chief Orchestrator",
                            "Product/Requirements Analyst",
                            "Software Architect",
                            "Software Engineer",
                            "Critic/QA",
                            "Technical Writer",
                            "Tech Lead Copilot",
                            "Daily Intelligence",
                            "Risk & Dependency Analyst",
                            "Delivery Forecast",
                        ],
                        definitions?.Items.Select(definition => definition.Name));
                    var chief = await client.GetFromJsonAsync<AgentContract>(
                        $"/api/v1/agents/{created.ChiefAgentId}", timeout.Token);
                    Assert.Equal(created.Id, chief?.ProjectId);
                    Assert.Equal("idle", chief?.State);
                    Assert.Equal(1, chief?.Lease?.FencingToken);
                    var projectAgents = await client.GetFromJsonAsync<AgentPage>(
                        $"/api/v1/agents?projectId={created.Id}", timeout.Token);
                    Assert.Equal(created.ChiefAgentId, Assert.Single(projectAgents!.Items).Id);
                    var orgChart = await client.GetFromJsonAsync<AgentOrgChartContract>(
                        $"/api/v1/projects/{created.Id}/agent-org-chart", timeout.Token);
                    Assert.Equal(created.Id, orgChart?.ProjectId);
                    Assert.Equal(created.ChiefAgentId, orgChart?.RootAgentId);
                    var rootNode = Assert.Single(orgChart!.Nodes);
                    Assert.Equal(created.ChiefAgentId, rootNode.AgentId);
                    Assert.Null(rootNode.ParentAgentId); Assert.Equal(0, rootNode.Level);
                    Assert.Equal(0, rootNode.Order); Assert.Equal("chief", rootNode.Role);
                    Assert.Equal("idle", rootNode.State); Assert.NotEmpty(rootNode.SkillIds);
                    Assert.NotNull(rootNode.EffectiveModelId);
                    Assert.Equal("active", created.State);
                    // Fase 1E: o projeto nasce autonomo. Quem quiser conduzir card a card muda o
                    // modo depois, por escolha explicita; o padrao nao decide isso pelo dono.
                    Assert.Equal("autonomous", created.OperationMode);
                    Assert.Equal([profileId], created.MemberProfileIds);
                    Assert.Equal(1, created.ConfigVersion);

                    await SeedReadyTaskAsync(
                        app.Services,
                        profileId,
                        projectId,
                        timeout.Token);

                    using var duplicate = await client.PostAsJsonAsync(
                        "/api/v1/projects",
                        Request(organization.Id, "poseidon"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

                    using var rename = new StringContent(
                        "{\"name\":\"Poseidon Labs\"}", Encoding.UTF8, "application/json");
                    using var renamedResponse = await client.PatchAsync(
                        $"/api/v1/projects/{projectId}", rename, timeout.Token);
                    var renamed = await renamedResponse.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                    Assert.Equal(1, renamed?.ConfigVersion);

                    using var configure = new StringContent(
                        "{\"repositoryUrl\":\"https://github.com/example/poseidon\",\"repositoryProvider\":\"github\",\"technologies\":[\".NET\",\"React\"]}",
                        Encoding.UTF8,
                        "application/json");
                    using var configuredResponse = await client.PatchAsync(
                        $"/api/v1/projects/{projectId}", configure, timeout.Token);
                    var configured = await configuredResponse.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                    Assert.Equal(2, configured?.ConfigVersion);
                    Assert.Equal([".NET", "React"], configured?.Technologies);

                    var digest = await client.GetFromJsonAsync<ProjectStatusDigestContract>(
                        $"/api/v1/projects/{projectId}/status-digest",
                        timeout.Token);
                    var digestReplay = await client.GetFromJsonAsync<ProjectStatusDigestContract>(
                        $"/api/v1/projects/{projectId}/status-digest",
                        timeout.Token);
                    Assert.NotNull(digest);
                    Assert.Equal(1, digest.TaskCounts.Ready);
                    Assert.Equal(1, digest.TaskCounts.Total);
                    Assert.Equal((0m, 0m, 0m),
                        (digest.Progress.Executed, digest.Progress.Validated, digest.Progress.Approved));
                    Assert.Equal("reviewPhase", digest.NextAction);
                    Assert.Contains(digest.RecentActivity, activity => activity.Action == "task.created");
                    Assert.Equal(digest.Fingerprint, digestReplay?.Fingerprint);

                    var page = await client.GetFromJsonAsync<ProjectPage>(
                        "/api/v1/projects?limit=1", timeout.Token);
                    Assert.Single(page?.Items ?? []);

                    var snapshot = await WaitForCreatedEventAsync(client, projectId, timeout.Token);
                    Assert.Contains(snapshot.Delta, item => item.Type == "project.created");
                }
                finally
                {
                    await app.StopAsync(timeout.Token);
                }
            }

            await using var restarted = CreateHost(databasePath);
            await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = GetBaseAddress(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                var recovered = await client.GetFromJsonAsync<ProjectResponse>(
                    $"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal("Poseidon Labs", recovered?.Name);
                Assert.Equal(2, recovered?.ConfigVersion);
                var recoveredChief = await client.GetFromJsonAsync<AgentContract>(
                    $"/api/v1/agents/{recovered?.ChiefAgentId}", timeout.Token);
                Assert.Equal(projectId, recoveredChief?.ProjectId);
                Assert.Equal(1, recoveredChief?.Lease?.FencingToken);
                var recoveredChart = await client.GetFromJsonAsync<AgentOrgChartContract>(
                    $"/api/v1/projects/{projectId}/agent-org-chart", timeout.Token);
                Assert.Equal(recovered?.ChiefAgentId, recoveredChart?.RootAgentId);

                using var deleted = await client.DeleteAsync($"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                using var missing = await client.GetAsync($"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            finally
            {
                await restarted.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static CreateProjectRequest Request(string organizationId, string key) =>
        new()
        {
            OrganizationId = organizationId,
            Name = $"Project {key}",
            Key = key,
            Description = "Backend project",
            Criticality = "high",
            DefaultBranch = "develop",
            Technologies = [".NET", "SQLite"],
        };

    private static async Task<EventStreamSnapshot> WaitForCreatedEventAsync(
        HttpClient client,
        string projectId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                $"/api/v1/event-streams/snapshot?stream=project:{projectId}",
                cancellationToken);
            if (snapshot is not null && snapshot.Delta.Any(item => item.Type == "project.created"))
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException("project.created was not dispatched.");
    }

    private static async Task SeedReadyTaskAsync(
        IServiceProvider services,
        string profileId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var profile = await services.GetRequiredService<ILocalProfileStore>()
            .GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("Created profile was not persisted.");
        var now = DateTimeOffset.UtcNow;
        var solicitationId = UlidValue.New(now).ToString();
        var demandId = UlidValue.New(now.AddTicks(1)).ToString();
        var taskId = UlidValue.New(now.AddTicks(2)).ToString();
        var instructionId = UlidValue.New(now.AddTicks(3)).ToString();
        const string instruction = "Implement the cockpit digest.";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instruction)));
        var store = new SqliteWorkChainStore(
            services.GetRequiredService<SqliteWriteDispatcher>());
        var receipt = await store.CreateAsync(
            new WorkChainCreateCommand(
                profile.TenantId,
                projectId,
                profileId,
                solicitationId,
                "Expose a deterministic cockpit digest.",
                demandId,
                "Cockpit digest",
                "[\"Digest is deterministic\"]",
                taskId,
                "Implement digest",
                "medium",
                1m,
                instructionId,
                instruction,
                hash,
                $"cockpit-seed-{taskId}",
                now),
            cancellationToken);
        Assert.False(receipt.Replay);
        var lifecycle = new WorkTaskLifecycleCommand(
            profile.TenantId,
            solicitationId,
            taskId,
            "chief",
            profileId,
            "Cockpit task was triaged.",
            "test:triage",
            1,
            $"cockpit-triage-{taskId}",
            now.AddTicks(4));
        Assert.Equal(
            WorkChainMutationStatus.Applied,
            (await store.TriageTaskAsync(lifecycle, cancellationToken)).Status);
        Assert.Equal(
            WorkChainMutationStatus.Applied,
            (await store.MarkTaskReadyAsync(
                lifecycle with
                {
                    Reason = "Cockpit task satisfies the Definition of Ready.",
                    EvidenceReference = "test:dor",
                    ExpectedTaskVersion = 2,
                    IdempotencyKey = $"cockpit-ready-{taskId}",
                    OccurredAt = now.AddTicks(5),
                },
                cancellationToken)).Status);
    }

    private static WebApplication CreateHost(string databasePath) =>
        HostApplication.Build(
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", databasePath,
            ]);

    private static Uri GetBaseAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
