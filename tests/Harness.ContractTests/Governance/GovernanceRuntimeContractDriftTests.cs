using System.Text.Json;

namespace Harness.ContractTests.Governance;

public sealed class GovernanceRuntimeContractDriftTests
{
    [Fact]
    public void PublishedOpenApiContainsGovernanceRuntimeContracts()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "docs", "contracts", "openapi.json")));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[]
        {
            "/api/v1/governance-runtime/receipts",
            "/api/v1/governance-runtime/receipts/{turnId}",
            "/api/v1/governance-runtime/receipts/{turnId}/metrics",
            "/api/v1/governance-runtime/evaluations",
            "/api/v1/governance-runtime/stale-doc-findings",
            "/api/v1/governance-runtime/projects/{projectId}/hashline-patches",
            "/api/v1/governance-runtime/hashline-benchmark",
            "/api/v1/governance-runtime/executors",
        })
        {
            Assert.True(paths.TryGetProperty(path, out _), $"Missing OpenAPI path: {path}");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
