using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Organizations;
using Harness.Host.Observability;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Conversations;

public sealed class WhatsAppChannelAdapterTests
{
    private const string AppSecret = "test-app-secret";
    private const string VerifyToken = "test-verify-token";

    [Fact]
    public async Task ReceivesWebhookDeliversRepliesAndDeduplicatesWithoutExternalNetwork()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"whatsapp-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "whatsapp.db");
        var cookies = new CookieContainer();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = Listen(activities);
        Directory.CreateDirectory(root);
        await using var graphApi = new FakeGraphApiServer();
        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", database,
                "--Harness:AgentExecutors:Mode", "simulated",
                "--Harness:Channels:WhatsApp:AccessToken", "test-access-token",
                "--Harness:Channels:WhatsApp:PhoneNumberId", "1234567890",
                "--Harness:Channels:WhatsApp:VerifyToken", VerifyToken,
                "--Harness:Channels:WhatsApp:AppSecret", AppSecret,
                "--Harness:Channels:WhatsApp:GraphApiBaseUrl", graphApi.BaseUrl,
                "--Harness:Channels:WhatsApp:DeliveryInterval", "01:00:00",
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
                    new CreateChannelLinkRequest("whatsapp", "5511999999999", project.Id),
                    timeout.Token);
                linkResponse.EnsureSuccessStatusCode();
                var link = (await linkResponse.Content.ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;

                // Handshake de verificação do webhook (GET) exigido pela Meta Cloud API.
                using (var badToken = await client.GetAsync(
                    "/api/v1/channels/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=abc",
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Forbidden, badToken.StatusCode);
                }

                using (var handshake = await client.GetAsync(
                    $"/api/v1/channels/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=challenge-123",
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, handshake.StatusCode);
                    Assert.Equal("challenge-123", await handshake.Content.ReadAsStringAsync(timeout.Token));
                }

                var body = InboundMessageBody("5511999999999", "wamid.001", "Planeje a entrega via WhatsApp.");

                using (var unsigned = await PostWebhookAsync(client, body, signature: null, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
                }

                using (var badSignature = await PostWebhookAsync(client, body, "sha256=" + new string('0', 64), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, badSignature.StatusCode);
                }

                using (var accepted = await PostWebhookAsync(client, body, Sign(body), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                    var receipt = (await accepted.Content.ReadFromJsonAsync<WhatsAppWebhookReceipt>(timeout.Token))!;
                    var single = Assert.Single(receipt.Messages);
                    Assert.True(single.Linked);
                    Assert.False(single.Deduplicated);
                }

                await WaitForChiefReplyAsync(client, link.Id, timeout.Token);
                var service = app.Services.GetRequiredService<WhatsAppChannelBackgroundService>();
                await service.DeliverRepliesAsync(timeout.Token);
                var delivered = Assert.Single(graphApi.SentMessages);
                Assert.Equal("Bearer test-access-token", delivered.Authorization);
                Assert.StartsWith("/v21.0/1234567890/messages", delivered.Path, StringComparison.Ordinal);
                Assert.Equal("5511999999999", delivered.To);
                Assert.False(string.IsNullOrWhiteSpace(delivered.Text));

                // Reentrega proposital do mesmo wamid: zero duplicação de turno e de resposta.
                using (var replay = await PostWebhookAsync(client, body, Sign(body), timeout.Token))
                {
                    var receipt = (await replay.Content.ReadFromJsonAsync<WhatsAppWebhookReceipt>(timeout.Token))!;
                    Assert.True(Assert.Single(receipt.Messages).Deduplicated);
                }
                await service.DeliverRepliesAsync(timeout.Token);
                Assert.Single(graphApi.SentMessages);

                var messages = (await client.GetFromJsonAsync<ChannelMessagePage>(
                    $"/api/v1/channels/links/{link.Id}/messages?limit=200", timeout.Token))!;
                Assert.Equal(2, messages.Items.Count);

                // Número não vinculado recebe orientação de linking, sem criar turno.
                var unlinkedBody = InboundMessageBody("5511000000000", "wamid.900", "olá?");
                using (var unknown = await PostWebhookAsync(client, unlinkedBody, Sign(unlinkedBody), timeout.Token))
                {
                    var receipt = (await unknown.Content.ReadFromJsonAsync<WhatsAppWebhookReceipt>(timeout.Token))!;
                    Assert.False(Assert.Single(receipt.Messages).Linked);
                }
                Assert.Contains(
                    graphApi.SentMessages,
                    item => item.To == "5511000000000" &&
                        item.Text.Contains("5511000000000", StringComparison.Ordinal));

                // Tipo de mensagem não suportado (ex.: imagem): não cria turno nem responde.
                var unsupportedBody = InboundUnsupportedMessageBody("5511999999999", "wamid.777");
                using (var unsupported = await PostWebhookAsync(client, unsupportedBody, Sign(unsupportedBody), timeout.Token))
                {
                    var receipt = (await unsupported.Content.ReadFromJsonAsync<WhatsAppWebhookReceipt>(timeout.Token))!;
                    var entry = Assert.Single(receipt.Messages);
                    Assert.False(entry.Linked);
                    Assert.Null(entry.TurnId);
                }

                var channelSpans = activities
                    .Where(item => item.OperationName.StartsWith("poseidon.channel.", StringComparison.Ordinal))
                    .ToArray();
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "accepted"));
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "deduplicated"));
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "unlinked"));
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "delivered"));
                Assert.Contains(channelSpans, span => Equals(span.GetTagItem("channel.result"), "unsupported_type"));
                Assert.All(channelSpans, span =>
                    Assert.DoesNotContain(
                        span.TagObjects,
                        tag => tag.Value?.ToString()?.Contains(
                            "5511999999999", StringComparison.Ordinal) == true ||
                               tag.Value?.ToString()?.Contains(
                                   "test-access-token", StringComparison.Ordinal) == true ||
                               tag.Value?.ToString()?.Contains(
                                   AppSecret, StringComparison.Ordinal) == true));
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

    private static async Task<HttpResponseMessage> PostWebhookAsync(
        HttpClient client,
        byte[] body,
        string? signature,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/whatsapp/webhook")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature is not null)
        {
            request.Headers.Add("X-Hub-Signature-256", signature);
        }

        return await client.SendAsync(request, token);
    }

    private static string Sign(byte[] body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), body);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    private static byte[] InboundMessageBody(string from, string messageId, string text) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                messages = new[]
                                {
                                    new
                                    {
                                        from,
                                        id = messageId,
                                        type = "text",
                                        text = new { body = text },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        }));

    private static byte[] InboundUnsupportedMessageBody(string from, string messageId) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                messages = new[]
                                {
                                    new
                                    {
                                        from,
                                        id = messageId,
                                        type = "image",
                                        text = (object?)null,
                                    },
                                },
                            },
                        },
                    },
                },
            },
        }));

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

        throw new TimeoutException("The Chief reply did not reach the WhatsApp conversation.");
    }

    private sealed class FakeGraphApiServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _loop;

        public FakeGraphApiServer()
        {
            HttpListenerException? lastBindFailure = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = new HttpListener();
                var port = FreePort();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                }
                catch (HttpListenerException exception)
                {
                    lastBindFailure = exception;
                    try
                    {
                        candidate.Close();
                    }
                    catch (HttpListenerException)
                    {
                    }

                    continue;
                }

                _listener = candidate;
                BaseUrl = $"http://127.0.0.1:{port}";
                _loop = Task.Run(LoopAsync);
                return;
            }

            throw new InvalidOperationException(
                "No loopback port could be bound for the fake Graph API server.", lastBindFailure);
        }

        public string BaseUrl { get; }

        public ConcurrentBag<(string Authorization, string Path, string To, string Text)> SentMessages { get; } = [];

        private async Task LoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
                SentMessages.Add((
                    context.Request.Headers["Authorization"] ?? "",
                    context.Request.Url!.AbsolutePath,
                    document.RootElement.GetProperty("to").GetString()!,
                    document.RootElement.GetProperty("text").GetProperty("body").GetString()!));
                var bytes = Encoding.UTF8.GetBytes("""{"messages":[{"id":"wamid.sent"}]}""");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (HttpListenerException)
            {
                // HttpListener may revalidate a prefix already reused while closing on macOS.
            }

            try
            {
                await _loop;
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
            }

            _shutdown.Dispose();
        }
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
