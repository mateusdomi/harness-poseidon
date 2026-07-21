using System.Text.Json;

namespace Harness.ContractTests.Readiness;

/// <summary>
/// Fatia A publica o contrato de prontidão primeiro (ADR-017): este teste assere apenas o
/// lado backend (OpenAPI). A metade frontend (tipos TypeScript) é adicionada pelo commit
/// frontend desta rodada e reconciliada na rodada seguinte — ver GOLDEN_PATH_HANDOFF.md §4.
/// </summary>
public sealed class ReadinessContractDriftTests
{
    private static readonly string[] SnapshotFields =
        ["projectId", "overallState", "steps", "nextActions"];

    private static readonly string[] StepFields =
    [
        "step", "state", "executionMode", "capability", "messageCode",
        "relatedIds", "blockers", "nextAction",
    ];

    private static readonly string[] BlockerFields = ["code", "relatedIds"];
    private static readonly string[] NextActionFields = ["code", "route", "resourceId"];

    [Fact]
    public void OpenApiReadinessSnapshotMatchesPublishedContract()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement;

        var readiness = openApi.GetProperty("paths").GetProperty("/api/v1/projects/{projectId}/readiness");
        Assert.True(readiness.TryGetProperty("get", out _));

        var schemas = openApi.GetProperty("components").GetProperty("schemas");
        AssertFields(schemas, "ProjectReadinessSnapshot", SnapshotFields);
        AssertFields(schemas, "ReadinessStepContract", StepFields);
        AssertFields(schemas, "ReadinessBlocker", BlockerFields);
        AssertFields(schemas, "ReadinessNextAction", NextActionFields);
    }

    private static void AssertFields(JsonElement schemas, string schemaName, string[] expected)
    {
        var properties = schemas.GetProperty(schemaName).GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), properties);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
