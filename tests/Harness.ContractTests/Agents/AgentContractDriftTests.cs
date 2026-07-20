using System.Text.Json;

namespace Harness.ContractTests.Agents;

public sealed class AgentContractDriftTests
{
    private static readonly string[] DefinitionFields =
    [
        "id", "key", "name", "role", "specialty", "description", "defaultModelId", "skillIds", "toolIds",
        "persona", "mission", "operatingPrinciples", "deliverables", "qualityCriteria",
        "communicationStyle", "limitations", "version", "enabled", "archivedAt",
        "stacks", "defaultEffort", "preferredAccountId", "fallbackModelIds", "team",
        "actorCritic", "risk",
    ];

    private static readonly string[] AgentFields =
    [
        "id", "definitionId", "projectId", "name", "state", "currentTaskId", "modelId", "lease", "metrics", "lastHeartbeatAt",
        "accountId", "effort", "providerEffortValue", "fallbackModelIds", "selectionReason", "selectionUpdatedAt",
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
        Assert.True(paths.GetProperty("/api/v1/agent-definitions/{definitionId}/versions").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/agent-definitions").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/agent-definitions/{definitionId}").TryGetProperty("patch", out _));
        Assert.True(paths.GetProperty("/api/v1/agent-definitions/{definitionId}").TryGetProperty("delete", out _));
        Assert.True(paths.GetProperty("/api/v1/agent-definitions/{definitionId}/duplicate").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/agents").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/agents/{agentId}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/agents/{agentId}/selection").TryGetProperty("patch", out _));
        Assert.True(paths.GetProperty("/api/v1/projects/{projectId}/agent-org-chart").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/projects/{projectId}/chief/pause").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/projects/{projectId}/chief/resume").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/projects/{projectId}/chief/handoff").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/projects/{projectId}/chief/drain").TryGetProperty("post", out _));

        AssertFields(openApi, "AgentDefinitionContract", DefinitionFields);
        AssertFields(openApi, "AgentDefinitionVersionContract",
            ["id", "definitionId", "version", "snapshot", "actorProfileId", "createdAt"]);
        AssertFields(openApi, "AgentDefinitionSnapshotContract", DefinitionFields
            .Where(field => field is not ("id" or "version" or "enabled" or "archivedAt")));
        AssertFields(openApi, "AgentContract", AgentFields);
        AssertFields(openApi, "AgentOrgChartContract", ["projectId", "rootAgentId", "nodes"]);
        AssertFields(openApi, "AgentOrgChartNodeContract",
            ["agentId", "definitionId", "parentAgentId", "level", "order", "name", "role",
             "specialty", "state", "currentTaskId", "effectiveModelId", "skillIds", "toolIds",
             "metrics"]);

        var frontend = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "agents.ts"));
        AssertFrontendFields(frontend, "export const agentDefinitionSchema", "export type AgentDefinition", DefinitionFields.Take(9));
        AssertFrontendFields(frontend, "export const agentSchema", "export type Agent", AgentFields.Take(10));
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
