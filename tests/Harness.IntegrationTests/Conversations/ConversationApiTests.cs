using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Providers;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Sqlite;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Harness.IntegrationTests.Support;

namespace Harness.IntegrationTests.Conversations;

public sealed class ConversationApiTests
{
    [Fact]
    public async Task ExpiredChiefLeaseIsReacquiredAfterRestartAndOldFencingIsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"chief-recovery-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "chief.db");
        Directory.CreateDirectory(artifactRoot);
        string tenantId; string profileId; string projectId; string chiefAgentId; string conversationId;
        ChiefTurnLease staleLease;
        try
        {
            await using (var app = CreateHost(databasePath))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    using var client = new HttpClient { BaseAddress = GetBaseAddress(app.Services) };
                    var profile = await CreateProfileAsync(client, "Recovery", timeout.Token); profileId = profile.Id;
                    await ProviderCatalogTestSeed.SeedForLocalProfileAsync(app.Services, timeout.Token);
                    tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>().GetAsync(profileId, timeout.Token))!.TenantId;
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                    await WorkflowTestBinding.BindRecommendedAsync(client, project.Id, timeout.Token);
                    projectId = project.Id; chiefAgentId = project.ChiefAgentId;
                    using var created = await client.PostAsJsonAsync("/api/v1/conversations", new CreateConversationRequest(projectId, "Recovery"), timeout.Token);
                    created.EnsureSuccessStatusCode();
                    conversationId = (await created.Content.ReadFromJsonAsync<ConversationResponse>(timeout.Token))!.Id;
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            var now = DateTimeOffset.UtcNow;
            var turnId = UlidValue.New(now).ToString();
            var user = ConversationApplicationService.CreateUserMessage(
                UlidValue.New(now.AddMilliseconds(1)).ToString(), profileId,
                new CreateMessageRequest(conversationId, "Retome após o restart"), now.AddMilliseconds(1));
            await using (var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token))
            {
                Assert.Equal(0, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
                var store = new SqliteConversationStore(dispatcher);
                await store.EnqueueAsync(new ChiefTurnEnqueueCommand(
                    tenantId, projectId, conversationId, turnId, chiefAgentId,
                    new MessageRecord(tenantId, projectId, user.Id, conversationId, "user", profileId, null, user.Content, null, user.CreatedAt),
                    $"chief-turn:{turnId}", now), timeout.Token);
                staleLease = await store.AcquireAsync(
                    new ChiefTurnAcquireCommand(tenantId, turnId, "crashed-owner", now, TimeSpan.FromMilliseconds(1)),
                    timeout.Token);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);

            await using var restarted = CreateHost(databasePath);
            await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = GetBaseAddress(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                _ = await WaitForTurnAsync(client, conversationId, turnId, timeout.Token);
                var recovered = await ReadChiefPipelineAsync(restarted.Services, turnId, timeout.Token);
                Assert.Equal("completed", recovered.MailboxState);
                Assert.Equal(2, recovered.AttemptCount);
                Assert.Equal(2, recovered.FencingToken);
                await Assert.ThrowsAsync<ChiefTurnConflictException>(() =>
                    restarted.Services.GetRequiredService<IChiefTurnStore>().FailAsync(
                        new ChiefTurnFailCommand(staleLease, "stale", DateTimeOffset.UtcNow, false), timeout.Token));
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

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
                    await ProviderCatalogTestSeed.SeedForLocalProfileAsync(app.Services, timeout.Token);
                    profileId = profile.Id;
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                    await WorkflowTestBinding.BindRecommendedAsync(client, project.Id, timeout.Token);

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

                    var accounts = (await client.GetFromJsonAsync<AccountPage>(
                        "/api/v1/accounts", timeout.Token))!;
                    var models = (await client.GetFromJsonAsync<ModelPage>(
                        "/api/v1/models", timeout.Token))!;
                    var account = accounts.Items.Single(x => x.State == "active");
                    var compatibleModels = models.Items.Where(x =>
                        x.ProviderId == account.ProviderId && x.Enabled &&
                        x.Capabilities.Contains("chat")).ToArray();
                    using var turnResponse = await client.PostAsJsonAsync(
                        $"/api/v1/conversations/{conversationId}/turns",
                        new StartChatTurnRequest("Continue com segurança", account.Id,
                            compatibleModels[0].Id, "high", [compatibleModels[1].Id],
                            "Override explícito do teste integrado."),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
                    var handle = await turnResponse.Content
                        .ReadFromJsonAsync<ChatTurnHandle>(timeout.Token);
                    Assert.NotNull(handle);
                    Assert.Equal(conversationId, handle.ConversationId);

                    var snapshot = await WaitForTurnAsync(
                        client, conversationId, handle.TurnId, timeout.Token);
                    // C3/ADR-019: o ciclo de vida do turno é observável e ordenado. Os eventos
                    // de transporte (`message.received`, `turn.registered`, `execution.enqueued`)
                    // vêm antes da execução; `provider.invoked` marca a chamada real do
                    // provider e `model.responded` a resposta do modelo — distinta do
                    // `chat.turnCompleted`, que é conclusão de transporte.
                    // O ciclo emite estados granulares do Chief (reading_context/thinking/
                    // planning) como chief.turnStateChanged adicionais entre provider.invoked e
                    // chat.turnStarted — os marcadores de atividade que orientam o balão do chat.
                    Assert.Equal(
                        [
                            "message.appended",
                            "message.appended",
                            "message.received",
                            "turn.registered",
                            "execution.enqueued",
                            "chief.turnStateChanged",
                            "provider.invoked",
                            "chief.turnStateChanged",
                            "chief.turnStateChanged",
                            "chief.turnStateChanged",
                            "chief.turnStateChanged",
                            "chat.turnStarted",
                            "chat.turnChunk",
                            "chat.turnChunk",
                            "message.appended",
                            "chat.turnCompleted",
                            "model.responded",
                            "chief.turnStateChanged",
                        ],
                        snapshot.Delta.Select(item => item.Type));
                    // Sequência contígua sem lacuna nem duplicata em todo o ciclo.
                    Assert.Equal(
                        Enumerable.Range(1, 18).Select(value => (long)value),
                        snapshot.Delta.Select(item => item.Sequence));
                    var completed = snapshot.Delta
                        .Last(item => item.Type == "chat.turnCompleted").Payload;
                    Assert.Equal(handle.TurnId, completed.GetProperty("turnId").GetString());
                    Assert.Equal("stop", completed.GetProperty("finishReason").GetString());
                    // A resposta do modelo é evento próprio, com payload sanitizado.
                    var responded = snapshot.Delta
                        .Last(item => item.Type == "model.responded").Payload;
                    Assert.Equal(handle.TurnId, responded.GetProperty("turnId").GetString());
                    Assert.False(responded.TryGetProperty("content", out _));

                    var messages = await client.GetFromJsonAsync<MessagePage>(
                        $"/api/v1/messages?conversationId={conversationId}", timeout.Token);
                    Assert.Equal(3, messages?.Items.Count);
                    Assert.Equal(["user", "user", "chief"],
                        messages?.Items.Select(message => message.AuthorRole));
                    Assert.NotEmpty(messages?.Items.Single(message => message.AuthorRole == "chief").Content ?? "");

                    var auditCounts = await ReadChatAuditCountsAsync(
                        app.Services, timeout.Token);
                    Assert.Equal((3L, 1L), auditCounts);
                    var pipeline = await ReadChiefPipelineAsync(
                        app.Services, handle.TurnId, timeout.Token);
                    Assert.Equal("completed", pipeline.MailboxState);
                    var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                        .GetAsync(profileId, timeout.Token))!.TenantId;
                    var persistedTurn = await app.Services.GetRequiredService<IChiefTurnStore>()
                        .GetAsync(tenantId, handle.TurnId, timeout.Token);
                    Assert.NotNull(persistedTurn?.Selection);
                    Assert.Equal((account.Id, compatibleModels[0].Id, "high", "explicit"),
                        (persistedTurn!.Selection!.AccountId, persistedTurn.Selection.ModelId,
                            persistedTurn.Selection.Effort, persistedTurn.Selection.Source));
                    Assert.Equal([compatibleModels[1].Id], persistedTurn.Selection.FallbackModelIds);
                    Assert.NotNull(persistedTurn.Selection.EstimatedCostUsd);
                    Assert.NotNull(persistedTurn.Selection.QuotaRemainingUsd);
                    Assert.Equal(1, pipeline.AttemptCount);
                    Assert.NotNull(pipeline.ResponseMessageId);
                    Assert.Equal($"fake:{project.Id}", pipeline.SessionId);
                    Assert.Equal("idle", pipeline.ChiefState);
                    Assert.Null(pipeline.LeaseOwner);
                    Assert.Equal(1, pipeline.FencingToken);
                    Assert.True(pipeline.HasDigest);
                    Assert.Equal(1, pipeline.InboxCount);

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
                var recoveredPipeline = await ReadChiefPipelineAsync(
                    restarted.Services, null, timeout.Token);
                Assert.Equal("completed", recoveredPipeline.MailboxState);
                Assert.Equal(1, recoveredPipeline.FencingToken);

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

    private static Task<ChiefPipelineSnapshot> ReadChiefPipelineAsync(
        IServiceProvider services,
        string? turnId,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<SqliteWriteDispatcher>().ExecuteAsync(
            async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    """
                    SELECT m.state,m.attempt_count,m.response_message_id,m.session_id,
                           s.state,s.lease_owner_id,s.lease_fencing_token,
                           s.last_digest_json IS NOT NULL,
                           (SELECT COUNT(*) FROM inbox_messages i
                            WHERE i.tenant_id=m.tenant_id
                              AND i.idempotency_key='chief-turn:' || m.id)
                    FROM chief_turn_mailbox m
                    JOIN chief_states s ON s.tenant_id=m.tenant_id AND s.project_id=m.project_id
                    WHERE ($turn IS NULL OR m.id=$turn)
                    ORDER BY m.id DESC LIMIT 1;
                    """;
                query.Parameters.AddWithValue("$turn", turnId ?? (object)DBNull.Value);
                await using var reader = await query.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                return new ChiefPipelineSnapshot(
                    reader.GetString(0), reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6), reader.GetBoolean(7), reader.GetInt32(8));
            },
            cancellationToken);

    private static WebApplication CreateHost(string databasePath) =>
        HostApplication.Build(
            ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", databasePath, "--Harness:AgentExecutors:Mode", "simulated"]);

    private static Uri GetBaseAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    private sealed record ChiefPipelineSnapshot(
        string MailboxState,
        int AttemptCount,
        string? ResponseMessageId,
        string? SessionId,
        string ChiefState,
        string? LeaseOwner,
        long FencingToken,
        bool HasDigest,
        int InboxCount);
}
