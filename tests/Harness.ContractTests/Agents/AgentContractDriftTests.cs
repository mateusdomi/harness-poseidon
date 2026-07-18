using System.Text.Json;

namespace Harness.ContractTests.Agents;

public sealed class AgentContractDriftTests
{
    private static readonly string[] DefinitionFields =
    [
        "id", "key", "name", "role", "specialty", "description", "defaultModelId", "skillIds", "toolIds",
    ];

    private static readonly string[] AgentFields =
    [
        "id", "definitionId", "projectId", "name", "state", "currentTaskId", "modelId", "lease", "metrics", "lastHeartbeatAt",
    ];

    [Fact]
    public void OpenApiAgentCatalogMatchesFrontendSchemasAndRoutes()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement;
        var paths = openApi.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/agent-definitions").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/agent-definitions/{definitionId}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/agents").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/agents/{agentId}").TryGetProperty("get", out _));

        AssertFields(openApi, "AgentDefinitionContract", DefinitionFields);
        AssertFields(openApi, "AgentContract", AgentFields);

        var frontend = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "agents.ts"));
        AssertFrontendFields(frontend, "export const agentDefinitionSchema", "export type AgentDefinition", DefinitionFields);
        AssertFrontendFields(frontend, "export const agentSchema", "export type Agent", AgentFields);
    }

    private static void AssertFields(JsonElement openApi, string schema, IEnumerable<string> fields)
    {
        var properties = openApi.GetProperty("components").GetProperty("schemas")
            .GetProperty(schema).GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(fields.Order(StringComparer.Ordinal), properties);
    }

    private static void AssertFrontendFields(string source, string startMarker, string endMarker, IEnumerable<string> fields)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var block = source[start..end];
        Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
