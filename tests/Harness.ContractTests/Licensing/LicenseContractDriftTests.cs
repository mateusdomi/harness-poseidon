using System.Text.Json;

namespace Harness.ContractTests.Licensing;

public sealed class LicenseContractDriftTests
{
    private static readonly string[] LicenseFields =
        ["id", "state", "plan", "deviceId", "deviceName", "expiresAt", "gracePeriodEndsAt", "offlineMode", "lastValidatedAt"];
    private static readonly string[] EntitlementFields =
        ["id", "key", "description", "included", "limit"];

    [Fact]
    public void OpenApiMatchesFrontendLicenseContractsAndActivation()
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement; var paths = openApi.GetProperty("paths");
        AssertMethods(paths, "/api/v1/licenses", "get");
        AssertMethods(paths, "/api/v1/licenses/{id}", "get");
        AssertMethods(paths, "/api/v1/licenses/activation", "post");
        AssertMethods(paths, "/api/v1/entitlements", "get");
        AssertMethods(paths, "/api/v1/entitlements/{id}", "get");
        AssertSchema(openApi, "LicenseContract", LicenseFields);
        AssertSchema(openApi, "EntitlementContract", EntitlementFields);
        var system = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "system.ts"));
        AssertFrontend(system, "licenseSchema", "License", LicenseFields);
        AssertFrontend(system, "entitlementSchema", "Entitlement", EntitlementFields);
        var commands = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "commands.ts"));
        Assert.Contains("activateLicenseInputSchema", commands, StringComparison.Ordinal);
    }

    private static void AssertMethods(JsonElement paths, string path, params string[] methods) =>
        Assert.All(methods, method => Assert.True(paths.GetProperty(path).TryGetProperty(method, out _)));
    private static void AssertSchema(JsonElement openApi, string name, string[] fields)
    {
        var actual = openApi.GetProperty("components").GetProperty("schemas").GetProperty(name)
            .GetProperty("properties").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(fields.Order(StringComparer.Ordinal), actual);
    }
    private static void AssertFrontend(string text, string schema, string type, string[] fields)
    {
        var start = text.IndexOf($"export const {schema}", StringComparison.Ordinal);
        var end = text.IndexOf($"export type {type}", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start); var block = text[start..end];
        Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
