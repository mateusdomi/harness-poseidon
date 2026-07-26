using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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

public sealed class TelegramChannelAdapterTests
{
    [Fact]
    public async Task PollsRepliesAndDeduplicatesRedeliveredUpdatesWithoutExternalNetwork()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"telegram-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "telegram.db");
        var cookies = new CookieContainer();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = Listen(activities);
        Directory.CreateDirectory(root);
        await using var telegram = new FakeTelegramServer();

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
                using var linkResponse = await client.PostAsJsonAsync(
                    "/api/v1/channels/links",
                    new CreateChannelLinkRequest("telegram", "777001", project.Id),
                    timeout.Token);
                linkResponse.EnsureSuccessStatusCode();
                var link = (await linkResponse.Content
                    .ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;

                using var service = new TelegramChannelBackgroundService(
                    new TelegramChannelOptions
                    {
                        BotToken = "test-token",
                        ApiBaseUrl = telegram.BaseUrl,
                        PollInterval = TimeSpan.FromMilliseconds(50),
                    },
                    app.Services.GetRequiredService<IChannelLinkStore>(),
                    app.Services.GetRequiredService<ILocalProfileStore>(),
                    app.Services.GetRequiredService<IProjectStore>(),
                    app.Services.GetRequiredService<IChiefTurnStore>(),
                    app.Services.GetRequiredService<IConversationStore>(),
                    app.Services.GetRequiredService<ChannelOutputGateway>(),
                    SystemClock.Instance,
                    NullLogger<TelegramChannelBackgroundService>.Instance);

                // Priming: nada é reentregue do histórico anterior ao boot.
                await service.DeliverRepliesAsync(timeout.Token);

                telegram.EnqueueUpdate(
                    5001,
                    777001,
                    "Planeje a entrega.\nDEMANDA: Responder no Telegram | Resposta no canal de origem");
                await service.PollOnceAsync(timeout.Token);
                await WaitForChiefReplyAsync(client, link.Id, timeout.Token);
                await service.DeliverRepliesAsync(timeout.Token);
                var delivered = Assert.Single(telegram.SentMessages, m => m.ChatId == "777001");
                Assert.False(string.IsNullOrWhiteSpace(delivered.Text));

                // Reentrega proposital do MESMO update: zero duplicação de turno e de resposta.
                telegram.EnqueueUpdate(
                    5001,
                    777001,
                    "Planeje a entrega.\nDEMANDA: Responder no Telegram | Resposta no canal de origem");
                await service.PollOnceAsync(timeout.Token);
                await Task.Delay(300, timeout.Token);
                await service.DeliverRepliesAsync(timeout.Token);
                Assert.Single(telegram.SentMessages, m => m.ChatId == "777001");
                var messages = (await client.GetFromJsonAsync<ChannelMessagePage>(
                    $"/api/v1/channels/links/{link.Id}/messages?limit=200", timeout.Token))!;
                Assert.Equal(2, messages.Items.Count);

                // Chat não vinculado recebe orientação de linking, sem criar turno.
                telegram.EnqueueUpdate(6001, 999999, "olá?");
                await service.PollOnceAsync(timeout.Token);
                var guidance = Assert.Single(telegram.SentMessages, m => m.ChatId == "999999");
                Assert.Contains("999999", guidance.Text);

                var channelSpans = activities
                    .Where(item => item.OperationName.StartsWith(
                        "poseidon.channel.",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert.Contains(
                    channelSpans,
                    span => Equals(span.GetTagItem("channel.result"), "accepted"));
                Assert.Contains(
                    channelSpans,
                    span => Equals(span.GetTagItem("channel.result"), "deduplicated"));
                Assert.Contains(
                    channelSpans,
                    span => Equals(span.GetTagItem("channel.result"), "unlinked"));
                Assert.Contains(
                    channelSpans,
                    span => Equals(span.GetTagItem("channel.result"), "delivered"));
                Assert.All(channelSpans, span =>
                    Assert.DoesNotContain(
                        span.TagObjects,
                        tag => tag.Value?.ToString()?.Contains(
                            "777001",
                            StringComparison.Ordinal) == true ||
                               tag.Value?.ToString()?.Contains(
                                   "test-token",
                                   StringComparison.Ordinal) == true));
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

    private static async Task WaitForChiefReplyAsync(
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
                return;
            }

            await Task.Delay(25, token);
        }

        throw new TimeoutException("A resposta do Chief não chegou à conversa do canal.");
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

    private sealed class FakeTelegramServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly ConcurrentQueue<string> _pendingUpdateBatches = new();
        private readonly Task _loop;
        private readonly CancellationTokenSource _shutdown = new();

        public FakeTelegramServer()
        {
            // FreePort() solta a porta antes do bind do HttpListener; sob testes
            // paralelos (e o martelo de rate limit) outra conexão pode capturá-la
            // no intervalo — tenta portas novas até o bind vingar.
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
                    continue;
                }

                _listener = candidate;
                BaseUrl = $"http://127.0.0.1:{port}";
                _loop = Task.Run(LoopAsync);
                return;
            }

            throw new InvalidOperationException(
                "No loopback port could be bound for the fake Telegram server.", lastBindFailure);
        }

        public string BaseUrl { get; }

        public ConcurrentBag<(string ChatId, string Text)> SentMessages { get; } = [];

        public void EnqueueUpdate(long updateId, long chatId, string text) =>
            _pendingUpdateBatches.Enqueue(JsonSerializer.Serialize(new
            {
                ok = true,
                result = new[]
                {
                    new
                    {
                        update_id = updateId,
                        message = new
                        {
                            message_id = updateId,
                            chat = new { id = chatId },
                            text,
                        },
                    },
                },
            }));

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

                string body;
                if (context.Request.Url!.AbsolutePath.EndsWith("/getUpdates", StringComparison.Ordinal))
                {
                    body = _pendingUpdateBatches.TryDequeue(out var batch)
                        ? batch
                        : """{"ok":true,"result":[]}""";
                }
                else if (context.Request.Url.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
                    SentMessages.Add((
                        document.RootElement.GetProperty("chat_id").GetString()!,
                        document.RootElement.GetProperty("text").GetString()!));
                    body = """{"ok":true,"result":{}}""";
                }
                else
                {
                    body = """{"ok":false}""";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
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
                // O Close do HttpListener revalida os prefixos no macOS e pode
                // falhar se a porta já foi reutilizada; o listener já parou.
            }

            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }

            _shutdown.Dispose();
        }
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
