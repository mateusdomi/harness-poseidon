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
            "/api/v1/governance-runtime/learning-candidates",
            "/api/v1/governance-runtime/learning-candidates/metrics",
            "/api/v1/governance-runtime/learning-candidates/{candidateId}/evaluations",
            "/api/v1/governance-runtime/learning-candidates/{candidateId}/shadow",
            "/api/v1/governance-runtime/learning-candidates/{candidateId}/promotion",
            "/api/v1/governance-runtime/learning-candidates/{candidateId}/rollback",
        })
        {
            Assert.True(paths.TryGetProperty(path, out _), $"Missing OpenAPI path: {path}");
        }
    }

    [Fact]
    public void LearningResponseExamplesStayAlignedWithPublishedContract()
    {
        using var example = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "tests", "Harness.ContractTests", "Fixtures", "governance-learning.json")));
        var candidate = example.RootElement.GetProperty("candidate");
        Assert.Equal("rule", candidate.GetProperty("type").GetString());
        Assert.Equal("shadow", candidate.GetProperty("state").GetString());
        Assert.Equal(64, candidate.GetProperty("fingerprint").GetString()!.Length);
        Assert.True(candidate.GetProperty("shadowResult").GetProperty("sampleSize").GetInt32() > 0);
        var metrics = example.RootElement.GetProperty("metrics");
        Assert.True(metrics.TryGetProperty("regressionsAfterPromotion", out _));
        var realtime = example.RootElement.GetProperty("realtime");
        Assert.Equal("audit.eventAppended", realtime.GetProperty("type").GetString());
        Assert.Equal("learning.candidatePromoted",
            realtime.GetProperty("payload").GetProperty("auditEvent").GetProperty("action").GetString());
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
