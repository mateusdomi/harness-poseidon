using System.Text.Json;

namespace Harness.ContractTests.Projects;

public sealed class ProjectContractDriftTests
{
    private static readonly string[] ProjectFields =
    [
        "id", "organizationId", "name", "key", "description", "state", "criticality",
        "repositoryUrl", "repositoryProvider", "defaultBranch", "technologies", "brand",
        "memberProfileIds", "configVersion", "chiefAgentId", "operationMode", "prototyping", "createdAt",
        "lastActivityAt",
    ];

    private static readonly string[] DigestFields =
    [
        "projectId", "asOf", "progress", "taskCounts", "attention", "workflow",
        "nextAction", "recentActivity", "unavailableSignals", "fingerprint",
    ];

    [Fact]
    public void OpenApiProjectMatchesFrontendSchemaAndCrudRoutes()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement;
        var paths = openApi.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/projects").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/projects").TryGetProperty("post", out _));
        var item = paths.GetProperty("/api/v1/projects/{projectId}");
        Assert.True(item.TryGetProperty("get", out _));
        Assert.True(item.TryGetProperty("patch", out _));
        Assert.True(item.TryGetProperty("delete", out _));
        Assert.True(paths.GetProperty("/api/v1/projects/{projectId}/status-digest")
            .TryGetProperty("get", out _));

        var properties = openApi.GetProperty("components").GetProperty("schemas")
            .GetProperty("ProjectResponse").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ProjectFields.Order(StringComparer.Ordinal), properties);

        var digestProperties = openApi.GetProperty("components").GetProperty("schemas")
            .GetProperty("ProjectStatusDigestContract").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(DigestFields.Order(StringComparer.Ordinal), digestProperties);

        var frontend = File.ReadAllText(
            Path.Combine(root, "frontend", "src", "api", "contracts", "core.ts"));
        var start = frontend.IndexOf("export const projectSchema", StringComparison.Ordinal);
        var end = frontend.IndexOf("export type Project", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var block = frontend[start..end];
        Assert.All(ProjectFields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
