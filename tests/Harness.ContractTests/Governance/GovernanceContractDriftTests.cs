using System.Text.Json;

namespace Harness.ContractTests.Governance;

public sealed class GovernanceContractDriftTests
{
    [Fact]
    public void OpenApiMatchesAuditEventFrontendContractAndGovernanceRoutes()
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "openapi.json"))); var api = document.RootElement; var paths = api.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/audit-events").TryGetProperty("get", out _)); Assert.True(paths.GetProperty("/api/v1/audit-events/{id}").TryGetProperty("get", out _)); Assert.True(paths.GetProperty("/api/v1/audit-events/integrity").TryGetProperty("get", out _)); Assert.True(paths.GetProperty("/api/v1/audit-events/export").TryGetProperty("get", out _));
        var fields = new[] { "id", "actorKind", "actorId", "action", "targetType", "targetId", "detail", "occurredAt" }; var actual = api.GetProperty("components").GetProperty("schemas").GetProperty("AuditEventContract").GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(fields.Order(StringComparer.Ordinal), actual);
        var source = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "system.ts")); var start = source.IndexOf("export const auditEventSchema", StringComparison.Ordinal); var end = source.IndexOf("export type AuditEvent", start, StringComparison.Ordinal); var block = source[start..end]; Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }
    private static string FindRepositoryRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent; return directory?.FullName ?? throw new DirectoryNotFoundException(); }
}
