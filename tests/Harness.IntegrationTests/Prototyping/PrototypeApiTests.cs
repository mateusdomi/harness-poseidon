using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Prototyping;
using Harness.Host.Realtime;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Prototyping;

public sealed class PrototypeApiTests
{
    [Fact]
    public async Task ProjectWaiverPrototypeGalleryLifecycleEventsAndRestartAreConsistent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"prototypes-{Guid.NewGuid():N}"); var database = Path.Combine(root, "prototypes.db"); Directory.CreateDirectory(root); var cookies = new CookieContainer(); string profileId; string projectId; string prototypeId; string referenceId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services); using (var anon = new HttpClient { BaseAddress = address }) using (var denied = await anon.GetAsync("/api/v1/prototypes", timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies }; using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token)) { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }
                    string organizationId; using (var response = await client.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token)) { response.EnsureSuccessStatusCode(); organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id; }
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest { OrganizationId = organizationId, Name = "Console", Key = "CONSOLE", Description = "Console backend" }, timeout.Token)) { response.EnsureSuccessStatusCode(); var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!; projectId = project.Id; Assert.Equal("autonomousGeneration", project.Prototyping.Mode); }

                    using (var invalid = await client.PatchAsJsonAsync($"/api/v1/projects/{projectId}", new { prototyping = new { mode = "notApplicable", waiver = (object?)null } }, timeout.Token)) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                    using (var response = await client.PatchAsJsonAsync($"/api/v1/projects/{projectId}", new { prototyping = new { mode = "notApplicable", waiver = new { reason = "Backend-only scope", grantedAt = "2026-07-18T22:40:00Z" } } }, timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal("Backend-only scope", (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))?.Prototyping.Waiver?.Reason); }
                    using (var blocked = await client.PostAsJsonAsync("/api/v1/prototypes", new PrototypeCreateRequest(projectId, "Cockpit", null, null), timeout.Token)) Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
                    using (var response = await client.PatchAsJsonAsync($"/api/v1/projects/{projectId}", new { prototyping = new { mode = "autonomousGeneration", waiver = (object?)null } }, timeout.Token)) response.EnsureSuccessStatusCode();

                    using (var response = await client.PostAsJsonAsync("/api/v1/prototypes", new PrototypeCreateRequest(projectId, "Cockpit", "Navigable cockpit", null), timeout.Token)) { response.EnsureSuccessStatusCode(); var value = (await response.Content.ReadFromJsonAsync<PrototypeContract>(timeout.Token))!; prototypeId = value.Id; Assert.Equal("draft", value.State); }
                    using (var response = await client.PostAsJsonAsync("/api/v1/visual-references", new VisualReferenceCreateRequest(projectId, "Dark telemetry", "https://example.test/dark.png", "url", prototypeId, ["dark", "telemetry", "dark"]), timeout.Token)) { response.EnsureSuccessStatusCode(); var value = (await response.Content.ReadFromJsonAsync<VisualReferenceContract>(timeout.Token))!; referenceId = value.Id; Assert.Equal(2, value.Tags.Count); }
                    using (var response = await client.PostAsJsonAsync($"/api/v1/prototypes/{prototypeId}/transitions", new PrototypeTransitionRequest("ready", null, "https://example.test/thumb.png"), timeout.Token)) response.EnsureSuccessStatusCode();
                    using (var response = await client.PostAsJsonAsync($"/api/v1/prototypes/{prototypeId}/transitions", new PrototypeTransitionRequest("published", "https://example.test/cockpit", null), timeout.Token)) { response.EnsureSuccessStatusCode(); Assert.Equal("published", (await response.Content.ReadFromJsonAsync<PrototypeContract>(timeout.Token))?.State); }
                    Assert.Single((await client.GetFromJsonAsync<PrototypePage>($"/api/v1/prototypes?projectId={projectId}", timeout.Token))!.Items); Assert.Single((await client.GetFromJsonAsync<VisualReferencePage>($"/api/v1/visual-references?projectId={projectId}", timeout.Token))!.Items);
                    var snapshot = await WaitForEventsAsync(client, projectId, timeout.Token); Assert.Contains(snapshot.Delta, x => x.Type == "prototype.created"); Assert.Equal(2, snapshot.Delta.Count(x => x.Type == "prototype.stateChanged"));
                }
                finally { await app.StopAsync(timeout.Token); }
            }
            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try { using var client = new HttpClient { BaseAddress = Address(restarted.Services) }; client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}"); Assert.Equal("published", (await client.GetFromJsonAsync<PrototypeContract>($"/api/v1/prototypes/{prototypeId}", timeout.Token))?.State); Assert.Equal(referenceId, (await client.GetFromJsonAsync<VisualReferenceContract>($"/api/v1/visual-references/{referenceId}", timeout.Token))?.Id); using var deleted = await client.DeleteAsync($"/api/v1/visual-references/{referenceId}", timeout.Token); Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task<EventStreamSnapshot> WaitForEventsAsync(HttpClient client, string projectId, CancellationToken token) { for (var i = 0; i < 200; i++) { var value = await client.GetFromJsonAsync<EventStreamSnapshot>($"/api/v1/event-streams/snapshot?stream=project:{projectId}", token); if (value is not null && value.Delta.Any(x => x.Type == "prototype.created") && value.Delta.Count(x => x.Type == "prototype.stateChanged") >= 2) return value; await Task.Delay(25, token); } throw new TimeoutException("Prototype events were not dispatched."); }
    private static WebApplication CreateHost(string database) => HostApplication.Build(["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services) { var values = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address."); return new Uri(values.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))); }
}
