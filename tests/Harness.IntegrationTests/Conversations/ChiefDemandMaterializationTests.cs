using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Host.WorkBoard;
using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Governance.Memory;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Harness.IntegrationTests.Support;

namespace Harness.IntegrationTests.Conversations;

public sealed class ChiefDemandMaterializationTests
{
    [Fact]
    public async Task ChiefTurnProposalsMaterializeDemandsAtomicallyAndSurviveRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"chief-demands-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "chief-demands.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);
        string projectId;

        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                    await CreateProfileAsync(client, timeout.Token);
                    await ProviderCatalogTestSeed.SeedForLocalProfileAsync(app.Services, timeout.Token);
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                    await WorkflowTestBinding.BindRecommendedAsync(client, project.Id, timeout.Token);
                    projectId = project.Id;
                    var localProfile = Assert.Single(
                        await app.Services.GetRequiredService<ILocalProfileStore>()
                            .ListAsync(timeout.Token));
                    const string memoryContent =
                        "Dogfood exige evidência auditável e rollout incremental.";
                    await app.Services.GetRequiredService<IVectorIndex>().IndexAsync(
                        new VectorDocumentRecord(
                            "chief-memory-1",
                            localProfile.TenantId,
                            projectId,
                            "project_decision",
                            memoryContent,
                            DeterministicLocalEmbedding.Embed(memoryContent),
                            new Dictionary<string, string>(),
                            DateTimeOffset.UtcNow),
                        timeout.Token);
                    using var created = await client.PostAsJsonAsync(
                        "/api/v1/conversations",
                        new CreateConversationRequest(projectId, "Dogfood"),
                        timeout.Token);
                    created.EnsureSuccessStatusCode();
                    var conversationId =
                        (await created.Content.ReadFromJsonAsync<ConversationResponse>(timeout.Token))!.Id;

                    using var turnResponse = await client.PostAsJsonAsync(
                        $"/api/v1/conversations/{conversationId}/turns",
                        new StartChatTurnRequest(
                            "Planeje o dogfood do produto.\n" +
                            "DEMANDA: Implementar login local | Adicionar autenticação por perfil local\n" +
                            "DEMANDA: Exportar relatório de auditoria"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
                    var handle = (await turnResponse.Content
                        .ReadFromJsonAsync<ChatTurnHandle>(timeout.Token))!;

                    await WaitForTurnAsync(client, conversationId, handle.TurnId, timeout.Token);
                    var contextSnapshot = await app.Services
                        .GetRequiredService<IGovernanceRuntimeStore>()
                        .GetContextSnapshotAsync(
                            localProfile.TenantId,
                            handle.TurnId,
                            timeout.Token);
                    Assert.NotNull(contextSnapshot);
                    Assert.Equal(projectId, contextSnapshot.ProjectId);
                    Assert.Equal(handle.TurnId, contextSnapshot.ExecutionId);
                    Assert.Contains(
                        contextSnapshot.Sources,
                        source =>
                            source.SourceId == "memory:chief-memory-1" &&
                            source.CitationReference == "project_decision:chief-memory-1");

                    var demands = (await client.GetFromJsonAsync<DemandPage>(
                        $"/api/v1/demands?projectId={projectId}", timeout.Token))!;
                    Assert.Equal(2, demands.Items.Count);
                    var login = Assert.Single(
                        demands.Items,
                        demand => demand.Title == "Implementar login local");
                    Assert.Equal("Adicionar autenticação por perfil local", login.Description);
                    Assert.Equal("medium", login.Priority);
                    Assert.Equal("open", login.State);
                    var export = Assert.Single(
                        demands.Items,
                        demand => demand.Title == "Exportar relatório de auditoria");
                    Assert.Equal("Exportar relatório de auditoria", export.Description);

                    var projectStream = (await client.GetFromJsonAsync<EventStreamSnapshot>(
                        $"/api/v1/event-streams/snapshot?stream=project:{projectId}",
                        timeout.Token))!;
                    Assert.Equal(
                        2,
                        projectStream.Delta.Count(item => item.Type == "demand.created"));
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
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = Address(restarted.Services) };
                    var demands = (await client.GetFromJsonAsync<DemandPage>(
                        $"/api/v1/demands?projectId={projectId}", timeout.Token))!;
                    Assert.Equal(2, demands.Items.Count);
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
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database, "--Harness:AgentExecutors:Mode", "simulated"]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
