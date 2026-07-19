using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Conversations;
using Harness.Host.Organizations;
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

public sealed class TeamsChannelAdapterTests
{
    [Fact]
    public async Task ReceivesRetriesRepliesAndDeduplicatesTeamsActivitiesWithoutExternalNetwork()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"teams-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "teams.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);
        await using var teamsProvider = new FakeTeamsServer();
        try
        {
            await using var app = HostApplication.Build(
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", database,
                "--Harness:Channels:Teams:InboundToken", "test-inbound",
                "--Harness:Channels:Teams:OutboundToken", "test-outbound",
                "--Harness:Channels:Teams:AllowedServiceHosts:0", "127.0.0.1",
                "--Harness:Channels:Teams:DeliveryInterval", "01:00:00",
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
                    new CreateChannelLinkRequest("teams", "aad-user-1", project.Id),
                    timeout.Token);
                linkResponse.EnsureSuccessStatusCode();
                var link = (await linkResponse.Content.ReadFromJsonAsync<ChannelLinkContract>(timeout.Token))!;

                var activity = Activity("activity-5001", "aad-user-1", teamsProvider.BaseUrl + "/amer/");
                using (var unauthorized = await client.PostAsJsonAsync(
                           "/api/v1/channels/teams/activities", activity, timeout.Token))
                    Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-inbound");
                using (var accepted = await client.PostAsJsonAsync(
                           "/api/v1/channels/teams/activities", activity, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                    var receipt = (await accepted.Content.ReadFromJsonAsync<TeamsActivityReceipt>(timeout.Token))!;
                    Assert.True(receipt.Linked);
                    Assert.False(receipt.Deduplicated);
                }

                await WaitForChiefReplyAsync(client, link.Id, timeout.Token);
                var service = app.Services.GetRequiredService<TeamsChannelBackgroundService>();
                await service.DeliverRepliesAsync(timeout.Token);
                var delivered = Assert.Single(teamsProvider.SentActivities);
                Assert.Equal("Bearer test-outbound", delivered.Authorization);
                Assert.StartsWith("/amer/v3/conversations/", delivered.Path, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(delivered.Text));
                Assert.Equal(2, teamsProvider.RequestCount); // 429 + retry 200.

                using (var replay = await client.PostAsJsonAsync(
                           "/api/v1/channels/teams/activities", activity, timeout.Token))
                {
                    var receipt = (await replay.Content.ReadFromJsonAsync<TeamsActivityReceipt>(timeout.Token))!;
                    Assert.True(receipt.Deduplicated);
                }
                await service.DeliverRepliesAsync(timeout.Token);
                Assert.Single(teamsProvider.SentActivities);

                var messages = (await client.GetFromJsonAsync<ChannelMessagePage>(
                    $"/api/v1/channels/links/{link.Id}/messages?limit=200", timeout.Token))!;
                Assert.Equal(2, messages.Items.Count);
                Assert.Contains("brief.txt", messages.Items.Single(item => item.AuthorRole == "user").Content);

                using (var invalidUrl = await client.PostAsJsonAsync(
                           "/api/v1/channels/teams/activities",
                           Activity("activity-5002", "aad-user-1", "https://not-allowlisted.invalid"),
                           timeout.Token))
                    Assert.Equal(HttpStatusCode.BadRequest, invalidUrl.StatusCode);

                using (var unknown = await client.PostAsJsonAsync(
                           "/api/v1/channels/teams/activities",
                           Activity("activity-6001", "aad-unknown", teamsProvider.BaseUrl),
                           timeout.Token))
                {
                    var receipt = (await unknown.Content.ReadFromJsonAsync<TeamsActivityReceipt>(timeout.Token))!;
                    Assert.False(receipt.Linked);
                }
                Assert.Contains(
                    teamsProvider.SentActivities,
                    item => item.Text.Contains("aad-unknown", StringComparison.Ordinal));
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static TeamsActivity Activity(string id, string aadObjectId, string serviceUrl) =>
        new(
            "message",
            id,
            serviceUrl,
            new TeamsChannelAccount("teams-user", aadObjectId),
            new TeamsConversationAccount("conversation-42"),
            "Planeje a resposta no Teams.",
            [new TeamsAttachment("text/plain", "brief.txt")]);

    private static async Task WaitForChiefReplyAsync(HttpClient client, string linkId, CancellationToken token)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var page = await client.GetFromJsonAsync<ChannelMessagePage>(
                $"/api/v1/channels/links/{linkId}/messages?limit=200", token);
            if (page?.Items.Any(message => message.AuthorRole == "chief") == true) return;
            await Task.Delay(25, token);
        }
        throw new TimeoutException("The Chief reply did not reach the Teams conversation.");
    }

    private sealed class FakeTeamsServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _loop;

        public FakeTeamsServer()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }
        public int RequestCount { get; private set; }
        public ConcurrentBag<(string Authorization, string Path, string Text)> SentActivities { get; } = [];

        private async Task LoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) when (_shutdown.IsCancellationRequested) { return; }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                RequestCount++;
                if (RequestCount == 1)
                {
                    context.Response.StatusCode = 429;
                    context.Response.Headers["Retry-After"] = "0";
                    context.Response.Close();
                    continue;
                }
                using var reader = new StreamReader(context.Request.InputStream);
                using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
                SentActivities.Add((
                    context.Request.Headers["Authorization"] ?? "",
                    context.Request.Url!.AbsolutePath,
                    document.RootElement.GetProperty("text").GetString()!));
                var bytes = Encoding.UTF8.GetBytes("{\"id\":\"sent\"}");
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
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            _listener.Close();
            try { await _loop; }
            catch (HttpListenerException) { }
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
