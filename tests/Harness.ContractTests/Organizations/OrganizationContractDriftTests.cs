using System.Text.Json;

namespace Harness.ContractTests.Organizations;

public sealed class OrganizationContractDriftTests
{
    private static readonly string[] OrganizationFields =
    [
        "id",
        "name",
        "slug",
        "plan",
        "brand",
        "defaultWorkflowTemplateIds",
        "templateKeys",
        "policies",
        "createdAt",
    ];

    [Fact]
    public void OpenApiOrganizationMatchesFrontendSchemaAndRequiredRoutes()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement;
        var paths = openApi.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/organizations").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/organizations").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/organizations/{organizationId}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/organizations/{organizationId}").TryGetProperty("patch", out _));

        var properties = openApi
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("OrganizationResponse")
            .GetProperty("properties")
            .EnumerateObject()
            .Select(item => item.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(OrganizationFields.Order(StringComparer.Ordinal), properties);

        var frontend = File.ReadAllText(
            Path.Combine(root, "frontend", "src", "api", "contracts", "core.ts"));
        var start = frontend.IndexOf("export const organizationSchema", StringComparison.Ordinal);
        var end = frontend.IndexOf("export type Organization", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var schemaBlock = frontend[start..end];
        Assert.All(OrganizationFields, field =>
            Assert.Contains($"{field}:", schemaBlock, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
