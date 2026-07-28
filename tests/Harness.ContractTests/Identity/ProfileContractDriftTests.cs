using System.Text.Json;

namespace Harness.ContractTests.Identity;

public sealed class ProfileContractDriftTests
{
    private static readonly string[] ProfileFields =
    [
        "id",
        "displayName",
        "email",
        "avatarUrl",
        "locale",
        "role",
        "createdAt",
        "lastActiveAt",
    ];

    [Fact]
    public void OpenApiProfileMatchesFrontendSchemaAndRequiredRoutes()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement;
        var paths = openApi.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/profiles").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/profiles").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/profiles/current").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/profiles/{profileId}").TryGetProperty("patch", out _));

        var properties = openApi
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProfileResponse")
            .GetProperty("properties")
            .EnumerateObject()
            .Select(item => item.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ProfileFields.Order(StringComparer.Ordinal), properties);

        var frontend = File.ReadAllText(
            Path.Combine(root, "frontend", "src", "api", "contracts", "core.ts"));
        var start = frontend.IndexOf("export const profileSchema", StringComparison.Ordinal);
        var end = frontend.IndexOf("export type Profile", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var profileBlock = frontend[start..end];
        Assert.All(ProfileFields, field =>
            Assert.Contains($"{field}:", profileBlock, StringComparison.Ordinal));
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
