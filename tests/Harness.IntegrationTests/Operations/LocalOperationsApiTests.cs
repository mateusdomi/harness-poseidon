using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Operations;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Operations;

public sealed class LocalOperationsApiTests
{
    [Fact]
    public async Task BackupRestoreAndDiagnosticsOperateOnRealLocalState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"operations-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "harness.db"); var catalog = Path.Combine(root, "catalog");
        Directory.CreateDirectory(root); var cookies = new CookieContainer();
        try
        {
            await using var app = CreateHost(database, catalog); await app.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);
                using (var anonymous = new HttpClient { BaseAddress = address })
                using (var denied = await anonymous.GetAsync("/api/v1/diagnostics", timeout.Token))
                    Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = address };
                using (var response = await client.PostAsJsonAsync("/api/v1/profiles",
                    new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token)) response.EnsureSuccessStatusCode();
                string organizationId;
                using (var response = await client.PostAsJsonAsync("/api/v1/organizations",
                    new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token))
                { response.EnsureSuccessStatusCode(); organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id; }
                string projectId;
                using (var response = await client.PostAsJsonAsync("/api/v1/projects",
                    new CreateProjectRequest { OrganizationId = organizationId, Name = "Before backup", Key = "BACKUP", Description = "Snapshot" }, timeout.Token))
                { response.EnsureSuccessStatusCode(); projectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!.Id; }

                var before = (await client.GetFromJsonAsync<DiagnosticsContract>("/api/v1/diagnostics", timeout.Token))!;
                Assert.Equal("http", before.ApiMode); Assert.Equal("available", before.RealtimeState);
                Assert.Equal("ok", before.Checks.Single(value => value.Key == "database").State);
                using var backupResponse = await client.PostAsync("/api/v1/backups", null, timeout.Token);
                Assert.Equal(HttpStatusCode.Created, backupResponse.StatusCode);
                var backup = (await backupResponse.Content.ReadFromJsonAsync<BackupHandle>(timeout.Token))!;
                Assert.True(UlidValue.TryParse(backup.BackupId, out _));
                string postBackupProjectId;
                using (var response = await client.PostAsJsonAsync("/api/v1/projects",
                    new CreateProjectRequest { OrganizationId = organizationId, Name = "After backup", Key = "AFTER", Description = "Must disappear" }, timeout.Token))
                { response.EnsureSuccessStatusCode(); postBackupProjectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!.Id; }
                using (var created = await client.GetAsync($"/api/v1/projects/{postBackupProjectId}", timeout.Token))
                    Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                using (var invalid = await client.PostAsync("/api/v1/backups/not-an-id/restore", null, timeout.Token))
                    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                using (var missing = await client.PostAsync(
                    $"/api/v1/backups/{UlidValue.New(DateTimeOffset.UtcNow.AddMinutes(1))}/restore", null, timeout.Token))
                    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                using (var restore = await client.PostAsync($"/api/v1/backups/{backup.BackupId}/restore", null, timeout.Token))
                    Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
                Assert.Equal("Before backup", (await client.GetFromJsonAsync<ProjectResponse>(
                    $"/api/v1/projects/{projectId}", timeout.Token))?.Name);
                using (var removed = await client.GetAsync($"/api/v1/projects/{postBackupProjectId}", timeout.Token))
                    Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
                var after = (await client.GetFromJsonAsync<DiagnosticsContract>("/api/v1/diagnostics", timeout.Token))!;
                Assert.Equal("ok", after.Checks.Single(value => value.Key == "backups").State);
                var audit = await WaitForRestoreAuditAsync(client, timeout.Token);
                Assert.Contains(audit.Delta, value => value.Type == "audit.eventAppended" &&
                    value.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == "backup.restored");
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<EventStreamSnapshot> WaitForRestoreAuditAsync(HttpClient client, CancellationToken token)
    {
        for (var index = 0; index < 200; index++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                "/api/v1/event-streams/snapshot?stream=global", token);
            if (snapshot?.Delta.Any(value => value.Type == "audit.eventAppended" &&
                    value.Payload.GetProperty("auditEvent").GetProperty("action").GetString() == "backup.restored") == true)
                return snapshot;
            await Task.Delay(25, token);
        }
        throw new TimeoutException("Restore audit was not dispatched.");
    }
    private static WebApplication CreateHost(string database, string catalog) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database,
         "--Harness:DocumentCatalogPath", catalog]);
    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
