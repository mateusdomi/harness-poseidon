using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Host.Profiles;
using Harness.Host.Providers;
using Harness.Host.Realtime;
using Harness.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Harness.IntegrationTests.Support;

namespace Harness.IntegrationTests.Providers;

public sealed class ProviderCatalogApiTests
{
    [Fact]
    public async Task TenantCatalogSupportsRoutingBudgetSyncEventsAndRestartWithoutPersistingSecrets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"providers-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "providers.db"); Directory.CreateDirectory(root);
        var cookies = new CookieContainer(); string profileId; string providerId; string accountId; string modelId; string budgetId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.GetAsync("/api/v1/providers", timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                    { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }

                    var providers = (await client.GetFromJsonAsync<ProviderPage>("/api/v1/providers", timeout.Token))!;
                    await ProviderCatalogTestSeed.SeedForLocalProfileAsync(app.Services, timeout.Token);
                    var accounts = (await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts", timeout.Token))!;
                    var models = (await client.GetFromJsonAsync<ModelPage>("/api/v1/models", timeout.Token))!;
                    var routing = (await client.GetFromJsonAsync<RoutingPolicyPage>("/api/v1/routing-policies", timeout.Token))!;
                    var budgets = (await client.GetFromJsonAsync<BudgetPage>("/api/v1/budgets", timeout.Token))!;
                    Assert.Equal(3, providers.Items.Count); Assert.Equal(2, accounts.Items.Count); Assert.Equal(4, models.Items.Count);
                    Assert.All(models.Items, model => Assert.Equal(["low", "medium", "high", "max"], model.EffortMappings.Select(value => value.Effort)));
                    Assert.Single(routing.Items); Assert.Equal(3, budgets.Items.Count);
                    Assert.DoesNotContain("keychain://", await client.GetStringAsync("/api/v1/accounts", timeout.Token), StringComparison.OrdinalIgnoreCase);
                    var definitions = (await client.GetFromJsonAsync<AgentDefinitionPage>("/api/v1/agent-definitions", timeout.Token))!;
                    Assert.All(definitions.Items, definition => Assert.Contains(definition.DefaultModelId, models.Items.Select(x => x.Id)));

                    providerId = providers.Items.Single(x => x.Kind == "openai").Id;
                    using (var invalid = await client.PostAsJsonAsync("/api/v1/accounts", new CreateProviderAccountRequest(providerId, "Unsafe", "https://example.test/secret", 10m), timeout.Token))
                        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                    string disposableAccountId;
                    using (var created = await client.PostAsJsonAsync("/api/v1/accounts", new CreateProviderAccountRequest(
                        providerId, "Disposable account", "keychain://harness/disposable", 25m,
                        "mateus@example.test", "pro", "apiKey", "monthly",
                        DateTimeOffset.Parse("2026-08-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                        ["chat", "code"]), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                        var value = (await created.Content.ReadFromJsonAsync<AccountContract>(timeout.Token))!;
                        disposableAccountId = value.Id; Assert.Equal("disabled", value.State);
                        Assert.Equal("mateus@example.test", value.Identity); Assert.Equal("pro", value.Plan);
                        Assert.Equal("apiKey", value.Authentication); Assert.Equal("unknown", value.Health);
                        Assert.Equal(["chat", "code"], value.Capabilities);
                        Assert.DoesNotContain("keychain://", await created.Content.ReadAsStringAsync(timeout.Token), StringComparison.OrdinalIgnoreCase);
                    }
                    using (var activated = await client.PatchAsJsonAsync($"/api/v1/accounts/{disposableAccountId}", new AccountPatchRequest(null, "active", null), timeout.Token))
                        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
                    using (var blocked = await client.DeleteAsync($"/api/v1/accounts/{disposableAccountId}", timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                    using (var disabled = await client.PatchAsJsonAsync($"/api/v1/accounts/{disposableAccountId}", new AccountPatchRequest(null, "disabled", null), timeout.Token))
                        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
                    using (var deleted = await client.DeleteAsync($"/api/v1/accounts/{disposableAccountId}", timeout.Token))
                        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/providers/{providerId}", new ProviderPatchRequest("OpenAI managed", null, true), timeout.Token))
                    { patch.EnsureSuccessStatusCode(); Assert.Equal("OpenAI managed", (await patch.Content.ReadFromJsonAsync<ProviderContract>(timeout.Token))?.Name); }
                    accountId = accounts.Items.Single(x => x.ProviderId == providerId).Id;
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/accounts/{accountId}", new AccountPatchRequest(
                        "Primary account", "active", 175m, "primary@example.test", "enterprise",
                        "oauth", "healthy", "weekly",
                        DateTimeOffset.Parse("2026-07-26T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                        ["chat", "reasoning", "tools"]), timeout.Token))
                    {
                        patch.EnsureSuccessStatusCode();
                        var value = await patch.Content.ReadFromJsonAsync<AccountContract>(timeout.Token);
                        Assert.Equal("Primary account", value?.Label); Assert.Equal(175m, value?.QuotaLimitUsd);
                        Assert.Equal("enterprise", value?.Plan); Assert.Equal("healthy", value?.Health);
                        Assert.Equal(["chat", "reasoning", "tools"], value?.Capabilities);
                    }
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/accounts/{accountId}", new AccountPatchRequest("Renamed account", null, null), timeout.Token))
                    {
                        patch.EnsureSuccessStatusCode();
                        var value = await patch.Content.ReadFromJsonAsync<AccountContract>(timeout.Token);
                        Assert.Equal("Renamed account", value?.Label); Assert.Equal(175m, value?.QuotaLimitUsd);
                    }
                    using (var invalid = await client.PatchAsJsonAsync($"/api/v1/accounts/{accountId}", new AccountPatchRequest(null, "unknown", null), timeout.Token))
                    { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
                    modelId = models.Items.Single(x => x.Name == "gpt-5-codex").Id;
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/models/{modelId}", new ModelPatchRequest("Codex primary", true), timeout.Token))
                    { patch.EnsureSuccessStatusCode(); Assert.Equal("Codex primary", (await patch.Content.ReadFromJsonAsync<ModelContract>(timeout.Token))?.DisplayName); }
                    using (var invalid = await client.PostAsJsonAsync("/api/v1/models", new CreateProviderModelRequest(
                        providerId, "bad-model", "Bad model", ["shell"], 0, null, null,
                        [new("extreme", "unsafe")]), timeout.Token))
                        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                    string disposableModelId;
                    using (var created = await client.PostAsJsonAsync("/api/v1/models", new CreateProviderModelRequest(
                        providerId, "custom-codex", "Custom Codex", ["code", "chat"], 128000,
                        0.002m, 0.007m, [new("low", "low"), new("high", "xhigh")]), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                        var value = (await created.Content.ReadFromJsonAsync<ModelContract>(timeout.Token))!;
                        disposableModelId = value.Id; Assert.False(value.Enabled);
                        Assert.Equal(["chat", "code"], value.Capabilities);
                    }
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/models/{disposableModelId}",
                        new ModelPatchRequest("Custom Codex v2", true, ["chat", "embeddings"],
                            256000, 0.003m, 0.009m, [new("medium", "balanced")]), timeout.Token))
                    {
                        patch.EnsureSuccessStatusCode();
                        var value = (await patch.Content.ReadFromJsonAsync<ModelContract>(timeout.Token))!;
                        Assert.True(value.Enabled); Assert.Equal(256000, value.ContextWindow);
                        Assert.Equal("balanced", Assert.Single(value.EffortMappings).ProviderValue);
                    }
                    using (var blocked = await client.DeleteAsync($"/api/v1/models/{disposableModelId}", timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                    using (var disabled = await client.PatchAsJsonAsync($"/api/v1/models/{disposableModelId}",
                        new ModelPatchRequest(null, false), timeout.Token))
                        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
                    using (var deleted = await client.DeleteAsync($"/api/v1/models/{disposableModelId}", timeout.Token))
                        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                    var policy = routing.Items.Single();
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/routing-policies/{policy.Id}",
                        new RoutingPolicyPatchRequest("Cost bounded", [new("code", modelId, [], 7.5m)], true), timeout.Token))
                    { patch.EnsureSuccessStatusCode(); var value = await patch.Content.ReadFromJsonAsync<RoutingPolicyContract>(timeout.Token); Assert.Equal(7.5m, Assert.Single(value!.Rules).MaxCostPerAttemptUsd); }
                    budgetId = budgets.Items.Single(x => x.Scope == "global").Id;
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/budgets/{budgetId}", new BudgetPatchRequest(250m, 75m), timeout.Token))
                    { patch.EnsureSuccessStatusCode(); var value = await patch.Content.ReadFromJsonAsync<BudgetContract>(timeout.Token); Assert.Equal(250m, value?.LimitUsd); Assert.Equal(75m, value?.AlertThresholdPct); }
                    using (var sync = await client.PostAsync($"/api/v1/providers/{providerId}/sync", null, timeout.Token))
                    { sync.EnsureSuccessStatusCode(); Assert.Equal(2, (await sync.Content.ReadFromJsonAsync<ModelContract[]>(timeout.Token))?.Length); }

                    var snapshot = await WaitForEventsAsync(client, accountId, budgetId, timeout.Token);
                    Assert.Contains(snapshot.Delta, x => x.Type == "quota.updated" && x.Payload.GetProperty("budgetId").GetString() == budgetId);
                    Assert.Contains(snapshot.Delta, x => x.Type == "quota.updated" && x.Payload.GetProperty("accountId").ValueKind == System.Text.Json.JsonValueKind.String);
                    Assert.Contains(snapshot.Delta, x => x.Type == "audit.eventAppended");
                }
                finally { await app.StopAsync(timeout.Token); }
            }
            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) }; client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                Assert.Equal("OpenAI managed", (await client.GetFromJsonAsync<ProviderContract>($"/api/v1/providers/{providerId}", timeout.Token))?.Name);
                var account = await client.GetFromJsonAsync<AccountContract>($"/api/v1/accounts/{accountId}", timeout.Token);
                Assert.Equal("Renamed account", account?.Label); Assert.Equal(175m, account?.QuotaLimitUsd);
                Assert.Equal("primary@example.test", account?.Identity); Assert.Equal("enterprise", account?.Plan);
                Assert.Equal("oauth", account?.Authentication); Assert.Equal("healthy", account?.Health);
                Assert.Equal("weekly", account?.QuotaWindow); Assert.Equal(["chat", "reasoning", "tools"], account?.Capabilities);
                Assert.Equal("Codex primary", (await client.GetFromJsonAsync<ModelContract>($"/api/v1/models/{modelId}", timeout.Token))?.DisplayName);
                Assert.Equal(250m, (await client.GetFromJsonAsync<BudgetContract>($"/api/v1/budgets/{budgetId}", timeout.Token))?.LimitUsd);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<EventStreamSnapshot> WaitForEventsAsync(HttpClient client, string accountId, string budgetId, CancellationToken token)
    { for (var i = 0; i < 200; i++) { var value = await client.GetFromJsonAsync<EventStreamSnapshot>("/api/v1/event-streams/snapshot?stream=global", token); if (value is not null && value.Delta.Any(x => x.Type == "quota.updated" && x.Payload.GetProperty("accountId").GetString() == accountId) && value.Delta.Any(x => x.Type == "quota.updated" && x.Payload.GetProperty("budgetId").GetString() == budgetId) && value.Delta.Any(x => x.Type == "audit.eventAppended")) return value; await Task.Delay(25, token); } throw new TimeoutException("Provider events were not dispatched."); }
    private static WebApplication CreateHost(string database) => HostApplication.Build(["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services) { var values = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address."); return new Uri(values.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))); }
}
