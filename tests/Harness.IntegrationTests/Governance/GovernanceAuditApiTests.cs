using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Governance;
using Harness.Host.Notifications;
using Harness.Host.Profiles;
using Harness.Modules.Identity.Contracts;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Governance;

public sealed class GovernanceAuditApiTests
{
    private static readonly string[] ActorKinds = ["user", "chief", "agent", "system"];

    [Fact]
    public async Task AuditProjectionFiltersVerifiesExportsMasksAndSurvivesRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"governance-{Guid.NewGuid():N}"); var database = Path.Combine(root, "governance.db"); Directory.CreateDirectory(root);
        var cookies = new CookieContainer(); string profileId; string auditId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services); using (var anonymous = new HttpClient { BaseAddress = address }) using (var denied = await anonymous.GetAsync("/api/v1/audit-events", timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies }; using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token)) { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }
                    using (var response = await client.PatchAsJsonAsync($"/api/v1/settings/{profileId}", new { theme = "dark" }, timeout.Token)) response.EnsureSuccessStatusCode();
                    using (var response = await client.PostAsJsonAsync("/api/v1/notifications", new NotificationCreateRequest(profileId, "info", "system", "Ready", "Ready", null, null), timeout.Token)) response.EnsureSuccessStatusCode();
                    auditId = await AppendSecretAuditAsync(app.Services.GetRequiredService<SqliteWriteDispatcher>(), profileId, timeout.Token);

                    var page = (await client.GetFromJsonAsync<AuditEventPage>("/api/v1/audit-events", timeout.Token))!; Assert.True(page.Items.Count >= 3); Assert.All(page.Items, item => Assert.Contains(item.ActorKind, ActorKinds));
                    var secret = (await client.GetFromJsonAsync<AuditEventContract>($"/api/v1/audit-events/{auditId}", timeout.Token))!; Assert.Equal("model.credentialUpdated", secret.Action); Assert.Contains("sk-super-secret", secret.Detail, StringComparison.Ordinal);
                    var filtered = (await client.GetFromJsonAsync<AuditEventPage>($"/api/v1/audit-events?action=model.credentialUpdated&actorKind=user&targetType=model&targetId=01ARZ3NDEKTSV4RRFFQ69G5FJ1", timeout.Token))!; Assert.Equal(auditId, Assert.Single(filtered.Items).Id);
                    var integrity = (await client.GetFromJsonAsync<AuditIntegrityContract>("/api/v1/audit-events/integrity", timeout.Token))!; Assert.True(integrity.Valid); Assert.Equal(integrity.EntryCount, integrity.LastSequence); Assert.Null(integrity.FailedSequence);

                    var json = await client.GetStringAsync("/api/v1/audit-events/export?format=json&action=model.credentialUpdated", timeout.Token); Assert.Contains("token=****", json, StringComparison.Ordinal); Assert.DoesNotContain("sk-super-secret", json, StringComparison.Ordinal);
                    var csv = await client.GetStringAsync("/api/v1/audit-events/export?format=csv&action=model.credentialUpdated", timeout.Token); Assert.StartsWith("id,occurredAt,actorKind,actorId,action,targetType,targetId,detail", csv, StringComparison.Ordinal); Assert.Contains("token=****", csv, StringComparison.Ordinal); Assert.DoesNotContain("sk-super-secret", csv, StringComparison.Ordinal);

                    var mutation = await Assert.ThrowsAsync<SqliteException>(() => app.Services.GetRequiredService<SqliteWriteDispatcher>().ExecuteAsync(async (connection, token) => { await using var command = connection.CreateCommand(); command.CommandText = "UPDATE audit_ledger SET event_type='tampered' WHERE id=$id;"; command.Parameters.AddWithValue("$id", auditId); await command.ExecuteNonQueryAsync(token); }, timeout.Token));
                    Assert.Equal(19, mutation.SqliteErrorCode); Assert.True((await client.GetFromJsonAsync<AuditIntegrityContract>("/api/v1/audit-events/integrity", timeout.Token))?.Valid);
                }
                finally { await app.StopAsync(timeout.Token); }
            }
            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try { using var client = new HttpClient { BaseAddress = Address(restarted.Services) }; client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}"); Assert.Equal("model.credentialUpdated", (await client.GetFromJsonAsync<AuditEventContract>($"/api/v1/audit-events/{auditId}", timeout.Token))?.Action); Assert.True((await client.GetFromJsonAsync<AuditIntegrityContract>("/api/v1/audit-events/integrity", timeout.Token))?.Valid); }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<string> AppendSecretAuditAsync(SqliteWriteDispatcher dispatcher, string profileId, CancellationToken token)
    {
        var at = DateTimeOffset.Parse("2026-07-18T22:30:00Z", CultureInfo.InvariantCulture); var id = UlidValue.New(at).ToString(); const string modelId = "01ARZ3NDEKTSV4RRFFQ69G5FJ1"; const string type = "model.credentialUpdated";
        await dispatcher.ExecuteAsync(async (connection, cancellationToken) =>
        {
            string tenant; long sequence; string previous; await using (var tail = connection.CreateCommand()) { tail.CommandText = "SELECT tenant_id,sequence,event_hash FROM audit_ledger ORDER BY sequence DESC LIMIT 1;"; await using var reader = await tail.ExecuteReaderAsync(cancellationToken); Assert.True(await reader.ReadAsync(cancellationToken)); tenant = reader.GetString(0); sequence = reader.GetInt64(1) + 1; previous = reader.GetString(2); }
            var payload = JsonSerializer.Serialize(new { auditEvent = new { id, actorKind = "user", actorId = profileId, action = type, targetType = "model", targetId = modelId, detail = "Credential rotated: token=sk-super-secret-123", occurredAt = at } }); var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at);
            await using var insert = connection.CreateCommand(); insert.CommandText = "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);"; insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$tenant", tenant); insert.Parameters.AddWithValue("$sequence", sequence); insert.Parameters.AddWithValue("$previous", previous); insert.Parameters.AddWithValue("$hash", hash); insert.Parameters.AddWithValue("$type", type); insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture)); await insert.ExecuteNonQueryAsync(cancellationToken);
        }, token); return id;
    }

    private static WebApplication CreateHost(string database) => HostApplication.Build(["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services) { var values = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address."); return new Uri(values.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))); }
}
