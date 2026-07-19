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
                    var accounts = (await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts", timeout.Token))!;
                    var models = (await client.GetFromJsonAsync<ModelPage>("/api/v1/models", timeout.Token))!;
                    var routing = (await client.GetFromJsonAsync<RoutingPolicyPage>("/api/v1/routing-policies", timeout.Token))!;
                    var budgets = (await client.GetFromJsonAsync<BudgetPage>("/api/v1/budgets", timeout.Token))!;
                    Assert.Equal(3, providers.Items.Count); Assert.Equal(2, accounts.Items.Count); Assert.Equal(4, models.Items.Count);
                    Assert.Single(routing.Items); Assert.Equal(3, budgets.Items.Count);
                    Assert.DoesNotContain("keychain://", await client.GetStringAsync("/api/v1/accounts", timeout.Token), StringComparison.OrdinalIgnoreCase);
                    var definitions = (await client.GetFromJsonAsync<AgentDefinitionPage>("/api/v1/agent-definitions", timeout.Token))!;
                    Assert.All(definitions.Items, definition => Assert.Contains(definition.DefaultModelId, models.Items.Select(x => x.Id)));

                    providerId = providers.Items.Single(x => x.Kind == "openai").Id;
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/providers/{providerId}", new ProviderPatchRequest("OpenAI managed", null, true), timeout.Token))
                    { patch.EnsureSuccessStatusCode(); Assert.Equal("OpenAI managed", (await patch.Content.ReadFromJsonAsync<ProviderContract>(timeout.Token))?.Name); }
                    accountId = accounts.Items.Single(x => x.ProviderId == providerId).Id;
                    using (var patch = await client.PatchAsJsonAsync($"/api/v1/accounts/{accountId}", new AccountPatchRequest("Primary account", "active", 175m), timeout.Token))
                    {
                        patch.EnsureSuccessStatusCode();
                        var value = await patch.Content.ReadFromJsonAsync<AccountContract>(timeout.Token);
                        Assert.Equal("Primary account", value?.Label); Assert.Equal(175m, value?.QuotaLimitUsd);
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
