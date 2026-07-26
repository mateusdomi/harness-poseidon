using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Conversations.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Conversations;

/// <summary>
/// Roteamento de último canal ativo sobre o estado REAL do banco: dois vínculos apontando para a
/// mesma conversa e a eleição acompanhando a última entrada registrada, sem entregar a resposta
/// da Bruna em todos os canais ao mesmo tempo.
/// </summary>
public sealed class ActiveChannelRoutingTests
{
    [Fact]
    public async Task LastInboundChannelWinsTheConversationOutputAndFollowsTheUser()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"active-channel-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "routing.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database,
                 "--Harness:AgentExecutors:Mode", "simulated"]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var profile = await CreateProfileAsync(client, timeout.Token);
                var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                    .GetAsync(profile.Id, timeout.Token))!.TenantId;
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);

                // Um vínculo nasce pelo gateway (com a própria conversa)...
                using var telegramResponse = await client.PostAsJsonAsync(
                    "/api/v1/channels/links",
                    new CreateChannelLinkRequest("telegram", "777001", project.Id),
                    timeout.Token);
                telegramResponse.EnsureSuccessStatusCode();
                var telegram = (await telegramResponse.Content
                    .ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;

                // ...e o segundo é criado apontando para a MESMA conversa, que é o cenário em que
                // o roteamento importa (o usuário alcança a mesma conversa por dois canais).
                var linkStore = app.Services.GetRequiredService<IChannelLinkStore>();
                var router = app.Services.GetRequiredService<ActiveChannelRouter>();
                var now = DateTimeOffset.UtcNow;
                var whatsapp = await linkStore.GetOrCreateAsync(
                    new ChannelLinkCreateCommand(
                        tenantId,
                        UlidValue.New(now).ToString(),
                        "whatsapp",
                        "5511999999999",
                        profile.Id,
                        project.Id,
                        telegram.ConversationId,
                        now),
                    timeout.Token);

                // Nenhum dos dois recebeu entrada ainda: a eleição é determinística, nunca ambígua.
                var initial = await router.SelectActiveAsync(
                    tenantId, telegram.ConversationId, timeout.Token);
                Assert.NotNull(initial);
                var firstDecision = initial.Id;
                Assert.Equal(
                    firstDecision,
                    (await router.SelectActiveAsync(
                        tenantId, telegram.ConversationId, timeout.Token))!.Id);

                // Entrada pelo Telegram: ele passa a ser o canal ativo da conversa.
                await linkStore.MarkInboundAsync(
                    tenantId, telegram.Id, now.AddMinutes(1), timeout.Token);
                Assert.Equal(
                    telegram.Id,
                    (await router.SelectActiveAsync(
                        tenantId, telegram.ConversationId, timeout.Token))!.Id);
                Assert.False(await router.IsActiveAsync(tenantId, whatsapp, timeout.Token));

                // O usuário migra para o WhatsApp: a saída acompanha a última entrada.
                await linkStore.MarkInboundAsync(
                    tenantId, whatsapp.Id, now.AddMinutes(2), timeout.Token);
                var active = await router.SelectActiveAsync(
                    tenantId, telegram.ConversationId, timeout.Token);
                Assert.Equal(whatsapp.Id, active!.Id);
                Assert.True(await router.IsActiveAsync(tenantId, whatsapp, timeout.Token));

                var telegramRecord = await linkStore.GetAsync(
                    tenantId, telegram.Id, timeout.Token);
                Assert.False(await router.IsActiveAsync(tenantId, telegramRecord!, timeout.Token));

                // last_inbound_at é durável: sobrevive à releitura do store.
                Assert.Equal(
                    now.AddMinutes(1).ToUnixTimeSeconds(),
                    telegramRecord!.LastInboundAt!.Value.ToUnixTimeSeconds());

                // Conversa com um único vínculo continua sempre ativa (sem regressão de canal só).
                using var soloResponse = await client.PostAsJsonAsync(
                    "/api/v1/channels/links",
                    new CreateChannelLinkRequest("email", "dono@empresa.invalid", project.Id),
                    timeout.Token);
                soloResponse.EnsureSuccessStatusCode();
                var solo = (await soloResponse.Content
                    .ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;
                var soloRecord = await linkStore.GetAsync(tenantId, solo.Id, timeout.Token);
                Assert.True(await router.IsActiveAsync(tenantId, soloRecord!, timeout.Token));
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
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<ProfileResponse> CreateProfileAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
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
