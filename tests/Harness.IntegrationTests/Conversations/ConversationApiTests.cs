using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Conversations;

public sealed class ConversationApiTests
{
    [Fact]
    public async Task ConversationMessagesAndTurnAreTenantScopedDurableAndSequenced()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"chat-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "chat.db");
        Directory.CreateDirectory(artifactRoot);
        var cookies = new CookieContainer();
        string profileId;
        string conversationId;

        try
        {
            await using (var app = CreateHost(databasePath))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = GetBaseAddress(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var unauthorized = await anonymous.GetAsync(
                               "/api/v1/conversations", timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                    }

                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    var profile = await CreateProfileAsync(client, "Mateus", timeout.Token);
                    profileId = profile.Id;
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);

                    using var missingProject = await client.PostAsJsonAsync(
                        "/api/v1/conversations",
                        new CreateConversationRequest("01ARZ3NDEKTSV4RRFFQ69G5FAV", "Missing"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);

                    using var createdResponse = await client.PostAsJsonAsync(
                        "/api/v1/conversations",
                        new CreateConversationRequest(project.Id, "Planejamento principal"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
                    var created = await createdResponse.Content
                        .ReadFromJsonAsync<ConversationResponse>(timeout.Token);
                    Assert.NotNull(created);
                    conversationId = created.Id;
                    Assert.Equal("active", created.State);
                    Assert.Equal(profileId, created.CreatedByProfileId);
                    Assert.Null(created.LastMessageAt);

                    using var directResponse = await client.PostAsJsonAsync(
                        "/api/v1/messages",
                        new CreateMessageRequest(conversationId, "Mensagem direta"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, directResponse.StatusCode);
                    var direct = await directResponse.Content
                        .ReadFromJsonAsync<MessageResponse>(timeout.Token);
                    Assert.Equal("user", direct?.AuthorRole);
                    Assert.Equal(profileId, direct?.AuthorProfileId);
                    Assert.Null(direct?.AuthorAgentId);

                    using var turnResponse = await client.PostAsJsonAsync(
                        $"/api/v1/conversations/{conversationId}/turns",
                        new StartChatTurnRequest("Continue com segurança"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
                    var handle = await turnResponse.Content
                        .ReadFromJsonAsync<ChatTurnHandle>(timeout.Token);
                    Assert.NotNull(handle);
                    Assert.Equal(conversationId, handle.ConversationId);

                    var snapshot = await WaitForTurnAsync(
                        client, conversationId, handle.TurnId, timeout.Token);
                    Assert.Equal(
                        [
                            "message.appended",
                            "message.appended",
                            "chat.turnStarted",
                            "chat.turnChunk",
                            "chat.turnChunk",
                            "message.appended",
                            "chat.turnCompleted",
                        ],
                        snapshot.Delta.Select(item => item.Type));
                    Assert.Equal(
                        Enumerable.Range(1, 7).Select(value => (long)value),
                        snapshot.Delta.Select(item => item.Sequence));
                    var completed = snapshot.Delta[^1].Payload;
                    Assert.Equal(handle.TurnId, completed.GetProperty("turnId").GetString());
                    Assert.Equal("stop", completed.GetProperty("finishReason").GetString());

                    var messages = await client.GetFromJsonAsync<MessagePage>(
                        $"/api/v1/messages?conversationId={conversationId}", timeout.Token);
                    Assert.Equal(3, messages?.Items.Count);
                    Assert.Equal(["user", "user", "chief"],
                        messages?.Items.Select(message => message.AuthorRole));
                    Assert.NotEmpty(messages?.Items.Single(message => message.AuthorRole == "chief").Content ?? "");

                    var auditCounts = await ReadChatAuditCountsAsync(
                        app.Services, timeout.Token);
                    Assert.Equal((3L, 1L), auditCounts);

                    var page = await client.GetFromJsonAsync<ConversationPage>(
                        $"/api/v1/conversations?projectId={project.Id}", timeout.Token);
                    Assert.Single(page?.Items ?? []);
                    Assert.NotNull(page?.Items[0].LastMessageAt);

                    using var forged = new HttpClient { BaseAddress = address };
                    forged.DefaultRequestHeaders.Add(
                        "Cookie", "harness.profile=01ARZ3NDEKTSV4RRFFQ69G5FAV");
                    using var isolated = await forged.GetAsync(
                        $"/api/v1/conversations/{conversationId}", timeout.Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, isolated.StatusCode);
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
                var recovered = await client.GetFromJsonAsync<ConversationResponse>(
                    $"/api/v1/conversations/{conversationId}", timeout.Token);
                Assert.NotNull(recovered?.LastMessageAt);
                var recoveredMessages = await client.GetFromJsonAsync<MessagePage>(
                    $"/api/v1/messages?conversationId={conversationId}", timeout.Token);
                Assert.Equal(3, recoveredMessages?.Items.Count);

                using var deleted = await client.DeleteAsync(
                    $"/api/v1/conversations/{conversationId}", timeout.Token);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                using var missing = await client.GetAsync(
                    $"/api/v1/conversations/{conversationId}", timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            finally
            {
                await restarted.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

    private static async Task<ProfileResponse> CreateProfileAsync(
        HttpClient client,
        string name,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest(name, null, null, "pt-BR"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProfileResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Profile response was empty.");
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrganizationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Organization response was empty.");
    }

    private static async Task<ProjectResponse> CreateProjectAsync(
        HttpClient client,
        string organizationId,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon Backend",
                Key = "POSEIDON",
                Description = "Backend principal",
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProjectResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Project response was empty.");
    }

    private static async Task<EventStreamSnapshot> WaitForTurnAsync(
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
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException("Chat turn events were not dispatched.");
    }

    private static Task<(long MessageAppended, long TurnCompleted)> ReadChatAuditCountsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<SqliteWriteDispatcher>().ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    """
                    SELECT
                        SUM(CASE WHEN event_type='message.appended' THEN 1 ELSE 0 END),
                        SUM(CASE WHEN event_type='chat.turnCompleted' THEN 1 ELSE 0 END)
                    FROM audit_ledger;
                    """;
                await using var reader = await query.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                return (reader.GetInt64(0), reader.GetInt64(1));
            },
            cancellationToken);

    private static WebApplication CreateHost(string databasePath) =>
        HostApplication.Build(
            ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", databasePath]);

    private static Uri GetBaseAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
