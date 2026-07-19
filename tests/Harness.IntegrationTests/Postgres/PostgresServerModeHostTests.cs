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
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Postgres;

public sealed class PostgresServerModeHostTests
{
    [Fact]
    public async Task HostBootsInServerModeAndRunsChatToDemandFlowOnPostgres()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"server-mode-{Guid.NewGuid():N}");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);
        await using var fixture = await PostgresSkipLockedPocTests.ManagedPostgresFixture
            .StartAsync(timeout.Token);

        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls",
                "http://127.0.0.1:0",
                "--Harness:DatabasePath",
                Path.Combine(root, "unused.db"),
                "--Harness:Database:Provider",
                "postgres",
                "--Harness:Database:ConnectionString",
                fixture.ConnectionString,
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var health = await client.GetFromJsonAsync<HealthPayload>("/health", timeout.Token);
                Assert.Equal("healthy", health!.Status);

                // Fluxo completo do MVP no PostgreSQL: perfil → org → projeto →
                // templates canônicos semeados → chat → Chief → demanda.
                await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                var templates = (await client.GetFromJsonAsync<Harness.Host.Workflows.WorkflowTemplatePage>(
                    "/api/v1/workflow-templates?limit=50", timeout.Token))!;
                Assert.Equal(6, templates.Items.Count);

                using var conversationResponse = await client.PostAsJsonAsync(
                    "/api/v1/conversations",
                    new CreateConversationRequest(project.Id, "Servidor"),
                    timeout.Token);
                conversationResponse.EnsureSuccessStatusCode();
                var conversationId = (await conversationResponse.Content
                    .ReadFromJsonAsync<ConversationResponse>(timeout.Token))!.Id;
                using var turnResponse = await client.PostAsJsonAsync(
                    $"/api/v1/conversations/{conversationId}/turns",
                    new StartChatTurnRequest(
                        "Modo servidor.\nDEMANDA: Operar em PostgreSQL | Fluxo completo no modo servidor"),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
                var handle = (await turnResponse.Content
                    .ReadFromJsonAsync<ChatTurnHandle>(timeout.Token))!;
                await WaitForTurnAsync(client, conversationId, handle.TurnId, timeout.Token);

                var demands = (await client.GetFromJsonAsync<DemandPage>(
                    $"/api/v1/demands?projectId={project.Id}", timeout.Token))!;
                var demand = Assert.Single(demands.Items);
                Assert.Equal("Operar em PostgreSQL", demand.Title);

                // Operações locais de SQLite respondem com o problema de modo servidor.
                using var backups = await client.PostAsync(
                    new Uri("/api/v1/backups", UriKind.Relative), null, timeout.Token);
                Assert.Equal(HttpStatusCode.Conflict, backups.StatusCode);
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

    private sealed record HealthPayload(string Status);

    private static async Task WaitForTurnAsync(
        HttpClient client,
        string conversationId,
        string turnId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 400; attempt++)
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

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("O turno do Chief não completou no modo servidor.");
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
                Description = "Modo servidor",
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
