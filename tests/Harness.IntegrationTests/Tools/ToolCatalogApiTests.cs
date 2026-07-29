using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Host.Profiles;
using Harness.Host.Realtime;
using Harness.Host.Tools;
using Harness.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Tools;

public sealed class ToolCatalogApiTests
{
    [Fact]
    public async Task CatalogMatchesAgentLinksPublishesToolStateAndPersistsPatchesAcrossRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"tools-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "tools.db"); Directory.CreateDirectory(root);
        var cookies = new CookieContainer(); string profileId; string toolId; string mcpId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.GetAsync("/api/v1/tools", timeout.Token))
                        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var profileResponse = await client.PostAsJsonAsync("/api/v1/profiles",
                        new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                    { profileResponse.EnsureSuccessStatusCode(); profileId = (await profileResponse.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }

                    var skills = (await client.GetFromJsonAsync<SkillPage>("/api/v1/skills", timeout.Token))!;
                    var tools = (await client.GetFromJsonAsync<ToolPage>("/api/v1/tools", timeout.Token))!;
                    var plugins = (await client.GetFromJsonAsync<PluginPage>("/api/v1/plugins", timeout.Token))!;
                    var servers = (await client.GetFromJsonAsync<McpServerPage>("/api/v1/mcp-servers", timeout.Token))!;
                    Assert.Equal(5, skills.Items.Count); Assert.Equal(6, tools.Items.Count);
                    Assert.Equal(2, plugins.Items.Count); Assert.Equal(2, servers.Items.Count);
                    Assert.All(plugins.Items, plugin => Assert.Single(plugin.ProvidesToolIds));
                    var knownSkills = skills.Items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                    var knownTools = tools.Items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                    var definitions = (await client.GetFromJsonAsync<AgentDefinitionPage>("/api/v1/agent-definitions?limit=10", timeout.Token))!;
                    // Todo id de skill/tool vinculado é conhecido no catálogo — para qualquer persona.
                    Assert.All(definitions.Items, definition =>
                    {
                        Assert.All(definition.SkillIds, id => Assert.Contains(id, knownSkills));
                        Assert.All(definition.ToolIds, id => Assert.Contains(id, knownTools));
                    });
                    // As personas de sistema têm skills/tools vinculados; as de Delivery (DEL-08) são
                    // "sob demanda" (definições de catálogo, sem vínculos por padrão).
                    Assert.All(
                        definitions.Items.Where(d => !d.Key.StartsWith("delivery-", StringComparison.Ordinal)),
                        definition =>
                        {
                            Assert.NotEmpty(definition.SkillIds); Assert.NotEmpty(definition.ToolIds);
                        });

                    toolId = tools.Items.Single(x => x.Key == "shell").Id;
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/tools/{toolId}",
                        new ComponentPatchRequest("disabled", null), timeout.Token))
                    { patch.EnsureSuccessStatusCode(); Assert.Equal("disabled", (await patch.Content.ReadFromJsonAsync<ToolContract>(timeout.Token))?.State); }

                    mcpId = servers.Items.Single(x => x.Name == "filesystem-mcp").Id;
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/mcp-servers/{mcpId}",
                        new ComponentPatchRequest("disabled", "harness-mcp-filesystem --workspace /safe"), timeout.Token))
                    { patch.EnsureSuccessStatusCode(); var server = await patch.Content.ReadFromJsonAsync<McpServerContract>(timeout.Token); Assert.Equal("disabled", server?.State); Assert.EndsWith("/safe", server?.Endpoint); }

                    var snapshot = await WaitForEventsAsync(client, timeout.Token);
                    var changed = snapshot.Delta.Single(x => x.Type == "tool.statusChanged").Payload;
                    Assert.Equal(toolId, changed.GetProperty("toolId").GetString()); Assert.Equal("enabled", changed.GetProperty("from").GetString()); Assert.Equal("disabled", changed.GetProperty("to").GetString());
                    Assert.Contains(snapshot.Delta, x => x.Type == "audit.eventAppended");
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                Assert.Equal("disabled", (await client.GetFromJsonAsync<ToolContract>($"/api/v1/tools/{toolId}", timeout.Token))?.State);
                var server = await client.GetFromJsonAsync<McpServerContract>($"/api/v1/mcp-servers/{mcpId}", timeout.Token);
                Assert.Equal("disabled", server?.State); Assert.EndsWith("/safe", server?.Endpoint);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<EventStreamSnapshot> WaitForEventsAsync(HttpClient client, CancellationToken token)
    {
        for (var i = 0; i < 200; i++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>("/api/v1/event-streams/snapshot?stream=global", token);
            if (snapshot is not null && snapshot.Delta.Any(x => x.Type == "tool.statusChanged") && snapshot.Delta.Any(x => x.Type == "audit.eventAppended")) return snapshot;
            await Task.Delay(25, token);
        }
        throw new TimeoutException("Tool catalog events were not dispatched.");
    }

    private static WebApplication CreateHost(string database) =>
        HostApplication.Build(["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services)
    {
        var values = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address.");
        return new Uri(values.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
