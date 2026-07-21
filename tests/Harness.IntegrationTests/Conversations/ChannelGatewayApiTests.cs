using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Governance;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Conversations;

public sealed class ChannelGatewayApiTests
{
    [Fact]
    public async Task LinksTerminalIdentityRepliesInChannelAndDeduplicatesRedelivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"channel-gateway-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "channels.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);

        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database, "--Harness:AgentExecutors:Mode", "simulated"]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);

                // Linking explícito de identidade do canal terminal; idempotente por identidade.
                using var linkResponse = await client.PostAsJsonAsync(
                    "/api/v1/channels/links",
                    new CreateChannelLinkRequest("terminal", "tty:mateus", project.Id),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);
                var link = (await linkResponse.Content
                    .ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;
                using (var relink = await client.PostAsJsonAsync(
                    "/api/v1/channels/links",
                    new CreateChannelLinkRequest("terminal", "tty:mateus", project.Id),
                    timeout.Token))
                {
                    var same = (await relink.Content
                        .ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;
                    Assert.Equal(link.Id, same.Id);
                    Assert.Equal(link.ConversationId, same.ConversationId);
                }

                // Mensagem do canal com id externo; o Chief responde no canal de origem.
                using var sendResponse = await client.PostAsJsonAsync(
                    $"/api/v1/channels/links/{link.Id}/messages",
                    new ChannelMessageRequest(
                        "provider-msg-001",
                        "Planeje a entrega.\nDEMANDA: Notificar por terminal | Expor resposta no canal"),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Accepted, sendResponse.StatusCode);
                var receipt = (await sendResponse.Content
                    .ReadFromJsonAsync<ChannelMessageReceipt>(timeout.Token))!;
                Assert.False(receipt.Deduplicated);
                Assert.Equal(link.ConversationId, receipt.ConversationId);

                var replies = await WaitForChiefReplyAsync(client, link.Id, timeout.Token);
                Assert.Contains(replies.Items, message => message.AuthorRole == "user");
                Assert.Contains(replies.Items, message => message.AuthorRole == "chief");
                var messageCountBeforeRedelivery = replies.Items.Count;

                // Reentrega proposital do webhook/provedor: zero duplicação.
                using (var redelivery = await client.PostAsJsonAsync(
                    $"/api/v1/channels/links/{link.Id}/messages",
                    new ChannelMessageRequest(
                        "provider-msg-001",
                        "Planeje a entrega.\nDEMANDA: Notificar por terminal | Expor resposta no canal"),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Accepted, redelivery.StatusCode);
                    var redelivered = (await redelivery.Content
                        .ReadFromJsonAsync<ChannelMessageReceipt>(timeout.Token))!;
                    Assert.True(redelivered.Deduplicated);
                    Assert.Equal(receipt.TurnId, redelivered.TurnId);
                }

                await Task.Delay(300, timeout.Token);
                var afterRedelivery = (await client.GetFromJsonAsync<ChannelMessagePage>(
                    $"/api/v1/channels/links/{link.Id}/messages?limit=200", timeout.Token))!;
                Assert.Equal(messageCountBeforeRedelivery, afterRedelivery.Items.Count);

                // A demanda proposta pelo Chief via canal chegou à cadeia de trabalho.
                var demands = (await client.GetFromJsonAsync<DemandPage>(
                    $"/api/v1/demands?projectId={project.Id}", timeout.Token))!;
                Assert.Single(demands.Items);
                Assert.Equal("Notificar por terminal", demands.Items[0].Title);

                var audit = (await client.GetFromJsonAsync<AuditEventPage>(
                    "/api/v1/audit-events?action=channel.linked&limit=50", timeout.Token))!;
                Assert.Single(audit.Items);
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

    private static async Task<ChannelMessagePage> WaitForChiefReplyAsync(
        HttpClient client,
        string linkId,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var page = await client.GetFromJsonAsync<ChannelMessagePage>(
                $"/api/v1/channels/links/{linkId}/messages?limit=200", token);
            if (page is not null && page.Items.Any(message => message.AuthorRole == "chief"))
            {
                return page;
            }

            await Task.Delay(25, token);
        }

        throw new TimeoutException("A resposta do Chief não chegou ao canal.");
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

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
