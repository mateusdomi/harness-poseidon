using System.Text.Json;

namespace Harness.ContractTests.Tools;

public sealed class ToolCatalogContractDriftTests
{
    [Theory]
    [InlineData("skills", "SkillContract", "skillSchema", "Skill", "id,key,name,description,version,state")]
    [InlineData("tools", "ToolContract", "toolSchema", "Tool", "id,key,name,description,kind,state")]
    [InlineData("plugins", "PluginContract", "pluginSchema", "Plugin", "id,key,name,version,description,state,providesToolIds")]
    [InlineData("mcp-servers", "McpServerContract", "mcpServerSchema", "McpServer", "id,name,transport,endpoint,state,toolCount")]
    public void OpenApiCatalogMatchesFrontendSchemasAndCrudRoutes(string route, string schema,
        string frontendSchema, string frontendType, string fieldCsv)
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement; var paths = openApi.GetProperty("paths");
        Assert.True(paths.GetProperty($"/api/v1/{route}").TryGetProperty("get", out _));
        var item = paths.GetProperty($"/api/v1/{route}/{{id}}");
        Assert.True(item.TryGetProperty("get", out _)); Assert.True(item.TryGetProperty("patch", out _));
        var fields = fieldCsv.Split(',');
        var actual = openApi.GetProperty("components").GetProperty("schemas").GetProperty(schema)
            .GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(fields.Order(StringComparer.Ordinal), actual);
        var frontend = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "agents.ts"));
        var start = frontend.IndexOf($"export const {frontendSchema}", StringComparison.Ordinal);
        var end = frontend.IndexOf($"export type {frontendType}", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start); var block = frontend[start..end];
        Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
