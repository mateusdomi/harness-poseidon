using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Notifications;
using Harness.Host.Profiles;
using Harness.Host.Realtime;
using Harness.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Notifications;

public sealed class NotificationSettingsApiTests
{
    private static readonly string[] MutedCategories = ["quota", "workflow"];

    [Fact]
    public async Task ProfileNotificationsCoalesceChangeStatusStreamAndSurviveRestartWithSettings()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"notifications-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "notifications.db"); Directory.CreateDirectory(root);
        var cookies = new CookieContainer(); string profileId; string groupedId; string directId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.GetAsync("/api/v1/notifications", timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies }; using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var created = await client.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                    { created.EnsureSuccessStatusCode(); profileId = (await created.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }

                    var settings = (await client.GetFromJsonAsync<SettingsPage>("/api/v1/settings", timeout.Token))!; var initial = Assert.Single(settings.Items);
                    Assert.Equal(profileId, initial.Id); Assert.Equal("pt-BR", initial.Language); Assert.True(initial.NotificationsEnabled);
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/settings/{profileId}", new { theme = "dark", notificationsEnabled = false, mutedCategories = MutedCategories, workingDirectory = "/tmp/harness-work" }, timeout.Token))
                    { patch.EnsureSuccessStatusCode(); var updated = await patch.Content.ReadFromJsonAsync<SettingsContract>(timeout.Token); Assert.Equal("dark", updated?.Theme); Assert.False(updated?.NotificationsEnabled); Assert.Equal(2, updated?.MutedCategories.Count); }

                    var group = new NotificationCreateRequest(profileId, "warning", "quota", "Budget alert", "First observation", "quota:global", "/budgets");
                    using (var response = await client.PostAsJsonAsync("/api/v1/notifications", group, timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, response.StatusCode); var value = await response.Content.ReadFromJsonAsync<NotificationContract>(timeout.Token); groupedId = value!.Id; Assert.Equal(1, value.DedupeCount); }
                    using (var response = await client.PostAsJsonAsync("/api/v1/notifications", group with { Severity = "critical", Body = "Second observation" }, timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, response.StatusCode); var value = await response.Content.ReadFromJsonAsync<NotificationContract>(timeout.Token); Assert.Equal(groupedId, value?.Id); Assert.Equal(2, value?.DedupeCount); Assert.Equal("critical", value?.Severity); }
                    using (var response = await client.PostAsJsonAsync("/api/v1/notifications", new NotificationCreateRequest(profileId, "info", "system", "Ready", "System is ready", null, null), timeout.Token))
                    { response.EnsureSuccessStatusCode(); directId = (await response.Content.ReadFromJsonAsync<NotificationContract>(timeout.Token))!.Id; }

                    var page = (await client.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", timeout.Token))!; Assert.Equal(2, page.Items.Count); Assert.Equal(2, page.Items.Single(x => x.Id == groupedId).DedupeCount);
                    using (var read = await client.PostAsJsonAsync("/api/v1/notifications/read", new NotificationStatusRequest([groupedId]), timeout.Token))
                    { read.EnsureSuccessStatusCode(); Assert.Equal(1, (await read.Content.ReadFromJsonAsync<NotificationStatusResult>(timeout.Token))?.Updated); }
                    using (var mute = await client.PostAsJsonAsync("/api/v1/notifications/mute", new NotificationStatusRequest([directId]), timeout.Token))
                    { mute.EnsureSuccessStatusCode(); Assert.Equal(1, (await mute.Content.ReadFromJsonAsync<NotificationStatusResult>(timeout.Token))?.Updated); }
                    var snapshot = await WaitForNotificationsAsync(client, profileId, timeout.Token); Assert.Equal(3, snapshot.Delta.Count(x => x.Type == "notification.created"));
                    Assert.All(snapshot.Delta.Where(x => x.Type == "notification.created"), x => Assert.Equal(profileId, x.Payload.GetProperty("notification").GetProperty("profileId").GetString()));
                    var audit = await WaitForAuditActionsAsync(client, timeout.Token);
                    var actions = audit.Delta.Where(x => x.Type == "audit.eventAppended").Select(x => x.Payload.GetProperty("auditEvent").GetProperty("action").GetString()).ToArray();
                    Assert.Contains("settings.updated", actions); Assert.Contains("notification.created", actions); Assert.Contains("notification.read", actions); Assert.Contains("notification.muted", actions);
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) }; client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                Assert.Equal("dark", (await client.GetFromJsonAsync<SettingsContract>($"/api/v1/settings/{profileId}", timeout.Token))?.Theme);
                Assert.Equal("read", (await client.GetFromJsonAsync<NotificationContract>($"/api/v1/notifications/{groupedId}", timeout.Token))?.Status);
                Assert.Equal("muted", (await client.GetFromJsonAsync<NotificationContract>($"/api/v1/notifications/{directId}", timeout.Token))?.Status);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<EventStreamSnapshot> WaitForNotificationsAsync(HttpClient client, string profileId, CancellationToken token)
    { for (var i = 0; i < 200; i++) { var value = await client.GetFromJsonAsync<EventStreamSnapshot>($"/api/v1/event-streams/snapshot?stream=profile:{profileId}", token); if (value is not null && value.Delta.Count(x => x.Type == "notification.created") >= 3) return value; await Task.Delay(25, token); } throw new TimeoutException("Notification events were not dispatched."); }
    private static async Task<EventStreamSnapshot> WaitForAuditActionsAsync(HttpClient client, CancellationToken token)
    { for (var i = 0; i < 200; i++) { var value = await client.GetFromJsonAsync<EventStreamSnapshot>("/api/v1/event-streams/snapshot?stream=global", token); var actions = value?.Delta.Where(x => x.Type == "audit.eventAppended").Select(x => x.Payload.GetProperty("auditEvent").GetProperty("action").GetString()).ToArray() ?? []; if (actions.Contains("settings.updated") && actions.Contains("notification.created") && actions.Contains("notification.read") && actions.Contains("notification.muted")) return value!; await Task.Delay(25, token); } throw new TimeoutException("Notification audit events were not dispatched."); }
    private static WebApplication CreateHost(string database) => HostApplication.Build(["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services) { var values = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address."); return new Uri(values.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))); }
}
