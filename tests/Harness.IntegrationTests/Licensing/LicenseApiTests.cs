using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Licensing;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Licensing.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Licensing;

public sealed class LicenseApiTests
{
    [Fact]
    public async Task ActivationEntitlementsAuditExpiryAndDataAccessSurviveRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"license-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "license.db"); Directory.CreateDirectory(root);
        var cookies = new CookieContainer(); string profileId; string projectId; string licenseId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.GetAsync("/api/v1/licenses", timeout.Token))
                        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles",
                        new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                    { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }
                    string organizationId;
                    using (var response = await client.PostAsJsonAsync("/api/v1/organizations",
                        new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token))
                    { response.EnsureSuccessStatusCode(); organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id; }
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects",
                        new CreateProjectRequest { OrganizationId = organizationId, Name = "Licensed", Key = "LICENSED", Description = "Data remains readable" }, timeout.Token))
                    { response.EnsureSuccessStatusCode(); projectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!.Id; }

                    var initial = (await client.GetFromJsonAsync<LicensePage>("/api/v1/licenses", timeout.Token))!;
                    var unlicensed = Assert.Single(initial.Items); licenseId = unlicensed.Id;
                    Assert.Equal("unlicensed", unlicensed.State);
                    Assert.Empty((await client.GetFromJsonAsync<EntitlementPage>("/api/v1/entitlements", timeout.Token))!.Items);
                    using (var invalid = await client.PostAsJsonAsync("/api/v1/licenses/activation",
                        new ActivateLicenseRequest("invalid"), timeout.Token))
                        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                    using var activatedResponse = await client.PostAsJsonAsync("/api/v1/licenses/activation",
                        new ActivateLicenseRequest("ABCD-1234-EFGH-5678"), timeout.Token);
                    activatedResponse.EnsureSuccessStatusCode();
                    var activated = (await activatedResponse.Content.ReadFromJsonAsync<LicenseContract>(timeout.Token))!;
                    Assert.Equal(licenseId, activated.Id); Assert.Equal("active", activated.State);
                    Assert.NotNull(activated.ExpiresAt); Assert.NotNull(activated.GracePeriodEndsAt);
                    var entitlements = (await client.GetFromJsonAsync<EntitlementPage>("/api/v1/entitlements", timeout.Token))!;
                    Assert.Equal(6, entitlements.Items.Count);
                    Assert.True(entitlements.Items.Single(
                        value => value.Key == "presentation.technical").Included);
                    Assert.False(entitlements.Items.Single(value => value.Key == "sso.oidc").Included);
                    var audit = await WaitForAuditAsync(client, timeout.Token);
                    var detail = audit.Delta.Single(value => value.Type == "audit.eventAppended" &&
                        value.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == "license.activated")
                        .Payload.GetProperty("auditEvent").GetProperty("detail").GetString()!;
                    Assert.Contains("ABCD-****-****-****", detail, StringComparison.Ordinal);
                    Assert.DoesNotContain("ABCD-1234-EFGH-5678", detail, StringComparison.Ordinal);
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await ExpireAsync(database, timeout.Token);
            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                var license = (await client.GetFromJsonAsync<LicensePage>("/api/v1/licenses", timeout.Token))!;
                Assert.Equal("expired", Assert.Single(license.Items).State);
                using var project = await client.GetAsync($"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, project.StatusCode);
                Assert.Equal(6, (await client.GetFromJsonAsync<EntitlementPage>(
                    "/api/v1/entitlements", timeout.Token))!.Items.Count);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task ExpireAsync(string database, CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={database}"); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText =
            "UPDATE licenses SET expires_at=$expired,grace_period_ends_at=$grace;";
        command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddDays(-15).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$grace", DateTimeOffset.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<EventStreamSnapshot> WaitForAuditAsync(HttpClient client, CancellationToken token)
    {
        for (var index = 0; index < 200; index++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                "/api/v1/event-streams/snapshot?stream=global", token);
            if (snapshot?.Delta.Any(value => value.Type == "audit.eventAppended" &&
                    value.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == "license.activated") == true)
                return snapshot;
            await Task.Delay(25, token);
        }
        throw new TimeoutException("License audit was not dispatched.");
    }
    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
