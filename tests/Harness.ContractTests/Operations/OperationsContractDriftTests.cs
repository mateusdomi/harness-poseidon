using System.Text.Json;

namespace Harness.ContractTests.Operations;

public sealed class OperationsContractDriftTests
{
    [Fact]
    public void OpenApiMatchesFrontendBackupAndDiagnosticsContracts()
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement; var paths = openApi.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/backups").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/backups/{id}/restore").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/diagnostics").TryGetProperty("get", out _));
        AssertSchema(openApi, "BackupHandle", ["backupId", "createdAt"]);
        AssertSchema(openApi, "DiagnosticCheck", ["key", "state", "detail"]);
        AssertSchema(openApi, "ProductDiagnostic", ["name", "version", "codename"]);
        AssertSchema(openApi, "DiagnosticsContract", ["product", "apiMode", "realtimeState", "checks", "generatedAt"]);
        var commands = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "commands.ts"));
        AssertFrontend(commands, "backupHandleSchema", "BackupHandle", ["backupId", "createdAt"]);
        AssertFrontend(commands, "diagnosticCheckSchema", "DiagnosticCheck", ["key", "state", "detail"]);
        AssertFrontend(commands, "diagnosticsSchema", "Diagnostics", ["product", "apiMode", "realtimeState", "checks", "generatedAt"]);
    }

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
