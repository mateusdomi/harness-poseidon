using System.Text.Json;

namespace Harness.ContractTests.RunTargets;

public sealed class RunTargetContractDriftTests
{
    [Fact]
    public void OpenApiMatchesFrontendRunTargetContractAndCommands()
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "openapi.json"))); var api = document.RootElement; var paths = api.GetProperty("paths"); Assert.True(paths.GetProperty("/api/v1/run-targets").TryGetProperty("get", out _)); Assert.True(paths.GetProperty("/api/v1/run-targets/{id}").TryGetProperty("get", out _)); foreach (var action in new[] { "start", "stop", "restart" }) Assert.True(paths.GetProperty($"/api/v1/run-targets/{{id}}/{action}").TryGetProperty("post", out _)); Assert.True(paths.GetProperty("/api/v1/projects/{projectId}/run-environment/cleanup").TryGetProperty("post", out _));
        var fields = new[] { "id", "projectId", "name", "kind", "url", "port", "state", "detectedAt", "lastCheckAt" }; var actual = api.GetProperty("components").GetProperty("schemas").GetProperty("RunTargetContract").GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(fields.Order(StringComparer.Ordinal), actual); var source = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "system.ts")); var start = source.IndexOf("export const runTargetSchema", StringComparison.Ordinal); var end = source.IndexOf("export type RunTarget", start, StringComparison.Ordinal); var block = source[start..end]; Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal)); Assert.DoesNotContain("executable", block, StringComparison.Ordinal); Assert.DoesNotContain("arguments", block, StringComparison.Ordinal);
    }
    private static string FindRepositoryRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent; return directory?.FullName ?? throw new DirectoryNotFoundException(); }
}
