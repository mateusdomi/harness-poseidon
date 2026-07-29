using System.Text.Json;

namespace Harness.ContractTests.Prototyping;

public sealed class PrototypeContractDriftTests
{
    [Theory]
    [InlineData("prototypes", "PrototypeContract", "prototypeSchema", "Prototype", "id,projectId,name,description,state,url,thumbnailUrl,sourceDocumentId,createdAt,updatedAt")]
    [InlineData("visual-references", "VisualReferenceContract", "visualReferenceSchema", "VisualReference", "id,projectId,prototypeId,title,imageUrl,source,tags,createdAt")]
    public void OpenApiMatchesFrontendContentContracts(string route, string schema, string marker, string type, string csv)
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "openapi.json"))); var api = document.RootElement; var paths = api.GetProperty("paths"); Assert.True(paths.GetProperty($"/api/v1/{route}").TryGetProperty("get", out _)); Assert.True(paths.GetProperty($"/api/v1/{route}").TryGetProperty("post", out _)); var item = paths.GetProperty($"/api/v1/{route}/{{id}}"); Assert.True(item.TryGetProperty("get", out _)); Assert.True(item.TryGetProperty("delete", out _));
        var fields = csv.Split(','); var actual = api.GetProperty("components").GetProperty("schemas").GetProperty(schema).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(fields.Order(StringComparer.Ordinal), actual); var source = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "content.ts")); var start = source.IndexOf($"export const {marker}", StringComparison.Ordinal); var end = source.IndexOf($"export type {type}", start, StringComparison.Ordinal); var block = source[start..end]; Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }

    [Fact]
    public void OpenApiMatchesThePrototypingStageAndDesignSystemBundleContracts()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var api = document.RootElement;
        var paths = api.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/projects/{id}/prototyping-stage")
            .TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/projects/{id}/design-system-bundle")
            .TryGetProperty("post", out _));

        var source = File.ReadAllText(
            Path.Combine(root, "frontend", "src", "api", "contracts", "content.ts"));
        foreach (var (schema, marker) in new[]
        {
            ("PrototypingStageContract", "prototypingStageSchema"),
            ("DesignSystemBundleContract", "designSystemBundleSchema"),
        })
        {
            var fields = api.GetProperty("components").GetProperty("schemas")
                .GetProperty(schema).GetProperty("properties")
                .EnumerateObject().Select(property => property.Name).ToArray();
            var start = source.IndexOf($"export const {marker}", StringComparison.Ordinal);
            Assert.True(start >= 0);
            var block = source[start..source.IndexOf("export type", start, StringComparison.Ordinal)];
            Assert.All(fields, field =>
                Assert.Contains($"{char.ToLowerInvariant(field[0])}{field[1..]}:", block, StringComparison.Ordinal));
        }
    }

    private static string FindRepositoryRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent; return directory?.FullName ?? throw new DirectoryNotFoundException(); }
}
