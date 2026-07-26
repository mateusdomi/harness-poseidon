using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Notifications;
using Harness.Host.Observability;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.Conversations;

public sealed class EmailChannelAdapterTests
{
    private const string InboundToken = "test-email-inbound";

    [Fact]
    public async Task ReceivesAuthenticatedInboundEmailRepliesOverRelayAndDeduplicates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"email-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "email.db");
        var cookies = new CookieContainer();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = Listen(activities);
        Directory.CreateDirectory(root);
        var secretSuffix = Guid.NewGuid().ToString("N");
        var hostVariable = $"HARNESS_TEST_SMTP_HOST_{secretSuffix}";
        var fromVariable = $"HARNESS_TEST_SMTP_FROM_{secretSuffix}";
        var passwordVariable = $"HARNESS_TEST_SMTP_PASSWORD_{secretSuffix}";
        Environment.SetEnvironmentVariable(hostVariable, "relay.invalid");
        Environment.SetEnvironmentVariable(fromVariable, "bruna@poseidon.invalid");
        Environment.SetEnvironmentVariable(passwordVariable, "relay-secret");
        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", database,
                "--Harness:AgentExecutors:Mode", "simulated",
                "--Harness:Channels:Email:InboundToken", InboundToken,
                "--Harness:Channels:Email:DeliveryInterval", "01:00:00",
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                using var linkResponse = await client.PostAsJsonAsync(
                    "/api/v1/channels/links",
                    new CreateChannelLinkRequest("email", "dono@empresa.invalid", project.Id),
                    timeout.Token);
                linkResponse.EnsureSuccessStatusCode();
                var link = (await linkResponse.Content.ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;

                // A saída usa um transporte gravador: nenhum socket SMTP é aberto no teste.
                var transport = new RecordingSmtpTransport();
                using var outbound = new EmailChannelBackgroundService(
                    new EmailChannelOptions { InboundToken = InboundToken },
                    new SmtpNotificationOptions
                    {
                        Enabled = true,
                        HostReference = $"env://{hostVariable}",
                        FromAddressReference = $"env://{fromVariable}",
                        PasswordReference = $"env://{passwordVariable}",
                    },
                    transport,
                    new EnvironmentSecretReferenceResolver(),
                    app.Services.GetRequiredService<IChannelLinkStore>(),
                    app.Services.GetRequiredService<ILocalProfileStore>(),
                    app.Services.GetRequiredService<IProjectStore>(),
                    app.Services.GetRequiredService<IChiefTurnStore>(),
                    app.Services.GetRequiredService<IConversationStore>(),
                    app.Services.GetRequiredService<ActiveChannelRouter>(),
                    SystemClock.Instance,
                    NullLogger<EmailChannelBackgroundService>.Instance);

                // Priming: histórico anterior ao boot não é reentregue.
                await outbound.DeliverRepliesAsync(timeout.Token);

                var message = new EmailInboundMessage(
                    "dono@empresa.invalid",
                    "<msg-001@empresa.invalid>",
                    "Planeje a entrega",
                    "DEMANDA: Responder por e-mail | Resposta no canal de origem");

                using (var unauthorized = await client.PostAsJsonAsync(
                    "/api/v1/channels/email/messages", message, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                }

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", InboundToken);
                using (var accepted = await client.PostAsJsonAsync(
                    "/api/v1/channels/email/messages", message, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                    var receipt = (await accepted.Content.ReadFromJsonAsync<EmailMessageReceipt>(timeout.Token))!;
                    Assert.True(receipt.Linked);
                    Assert.False(receipt.Deduplicated);
                }

                await WaitForChiefReplyAsync(client, link.Id, timeout.Token);
                await outbound.DeliverRepliesAsync(timeout.Token);
                var delivered = Assert.Single(transport.Envelopes);
                Assert.Equal("dono@empresa.invalid", delivered.ToAddress);
                Assert.Equal("bruna@poseidon.invalid", delivered.FromAddress);
                Assert.Equal("relay.invalid", delivered.Host);
                Assert.True(delivered.UseTls);
                Assert.False(string.IsNullOrWhiteSpace(delivered.Body));

                // Reentrega do mesmo Message-Id: zero duplicação de turno e de resposta.
                using (var replay = await client.PostAsJsonAsync(
                    "/api/v1/channels/email/messages", message, timeout.Token))
                {
                    var receipt = (await replay.Content.ReadFromJsonAsync<EmailMessageReceipt>(timeout.Token))!;
                    Assert.True(receipt.Deduplicated);
                }
                await outbound.DeliverRepliesAsync(timeout.Token);
                Assert.Single(transport.Envelopes);

                var messages = (await client.GetFromJsonAsync<ChannelMessagePage>(
                    $"/api/v1/channels/links/{link.Id}/messages?limit=200", timeout.Token))!;
                Assert.Equal(2, messages.Items.Count);
                Assert.Contains(
                    "Planeje a entrega",
                    messages.Items.Single(item => item.AuthorRole == "user").Content);

                // Endereço com caixa diferente casa o mesmo vínculo (e-mail é case-insensitive).
                using (var mixed = await client.PostAsJsonAsync(
                    "/api/v1/channels/email/messages",
                    new EmailInboundMessage(
                        "Dono@Empresa.Invalid",
                        "<msg-002@empresa.invalid>",
                        "Segunda mensagem",
                        "Continue a entrega."),
                    timeout.Token))
                {
                    var receipt = (await mixed.Content.ReadFromJsonAsync<EmailMessageReceipt>(timeout.Token))!;
                    Assert.True(receipt.Linked);
                    Assert.Equal(link.ConversationId, receipt.ConversationId);
                }

                // Remetente sem vínculo: nenhum turno e NENHUM e-mail de volta (anti-backscatter).
                var envelopesBefore = transport.Envelopes.Count;
                using (var unknown = await client.PostAsJsonAsync(
                    "/api/v1/channels/email/messages",
                    new EmailInboundMessage(
                        "estranho@fora.invalid",
                        "<msg-900@fora.invalid>",
                        "olá?",
                        "quem é você?"),
                    timeout.Token))
                {
                    var receipt = (await unknown.Content.ReadFromJsonAsync<EmailMessageReceipt>(timeout.Token))!;
                    Assert.False(receipt.Linked);
                    Assert.Null(receipt.TurnId);
                }
                Assert.Equal(envelopesBefore, transport.Envelopes.Count);

                // Envelope inválido (corpo em branco) é recusado com 400.
                using (var invalid = await client.PostAsJsonAsync(
                    "/api/v1/channels/email/messages",
                    new EmailInboundMessage("dono@empresa.invalid", "<msg-003@x.invalid>", "vazio", "   "),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                }

                var channelSpans = activities
                    .Where(item => item.OperationName.StartsWith("poseidon.channel.", StringComparison.Ordinal))
                    .ToArray();
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "accepted"));
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "deduplicated"));
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "unlinked"));
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "delivered"));
                Assert.All(channelSpans, span =>
                    Assert.DoesNotContain(
                        span.TagObjects,
                        tag => tag.Value?.ToString()?.Contains(
                            "dono@empresa.invalid", StringComparison.OrdinalIgnoreCase) == true ||
                               tag.Value?.ToString()?.Contains(
                                   InboundToken, StringComparison.Ordinal) == true ||
                               tag.Value?.ToString()?.Contains(
                                   "relay-secret", StringComparison.Ordinal) == true));
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostVariable, null);
            Environment.SetEnvironmentVariable(fromVariable, null);
            Environment.SetEnvironmentVariable(passwordVariable, null);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingSmtpTransport : ISmtpTransport
    {
        public ConcurrentBag<SmtpEnvelope> Envelopes { get; } = [];

        public Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken)
        {
            Envelopes.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PoseidonTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task WaitForChiefReplyAsync(HttpClient client, string linkId, CancellationToken token)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var page = await client.GetFromJsonAsync<ChannelMessagePage>(
                $"/api/v1/channels/links/{linkId}/messages?limit=200", token);
            if (page?.Items.Any(message => message.AuthorRole == "chief") == true)
            {
                return;
            }

            await Task.Delay(25, token);
        }

        throw new TimeoutException("The Chief reply did not reach the email conversation.");
    }

    private static async Task CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, CancellationToken token)
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
