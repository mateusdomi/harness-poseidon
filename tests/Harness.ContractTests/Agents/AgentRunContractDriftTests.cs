using System.Text.Json;

namespace Harness.ContractTests.Agents;

/// <summary>
/// CA-5: o contrato publicado do bootstrap governado precisa existir no OpenAPI canônico e
/// o evento de ciclo de vida precisa existir no catálogo de eventos.
/// </summary>
public sealed class AgentRunContractDriftTests
{
    private static readonly string RepositoryRoot = ResolveRepositoryRoot();

    private static JsonDocument Load(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, relativePath)));

    [Theory]
    [InlineData("/api/v1/agent-runs", "post")]
    [InlineData("/api/v1/agent-runs/{attemptId}", "get")]
    [InlineData("/api/v1/agent-runs/{attemptId}/cancel", "post")]
    [InlineData("/api/v1/agent-runs/{attemptId}/review", "post")]
    [InlineData("/api/v1/agent-runs/recovery", "post")]
    [InlineData("/api/v1/agent-accounts/doctor", "get")]
    public void ThePublishedOpenApiExposesTheGovernedAgentRunSurface(string path, string method)
    {
        using var openApi = Load("docs/contracts/openapi.json");
        var paths = openApi.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty(path, out var route), $"Missing route {path}.");
        Assert.True(route.TryGetProperty(method, out _), $"Missing {method} on {path}.");
    }

    [Fact]
    public void TheAgentRunLifecycleEventIsPublishedWithATypedPayload()
    {
        using var events = Load("docs/contracts/events.json");
        var root = events.RootElement;

        Assert.Contains(
            "agentRun.stateChanged",
            root.GetProperty("events").EnumerateArray().Select(item => item.GetString()));

        var payload = root.GetProperty("payloads").GetProperty("agentRun.stateChanged");
        Assert.Equal("object", payload.GetProperty("type").GetString());
        Assert.Equal(
            ["runId", "attemptId", "projectId", "state", "accountAlias", "role"],
            payload.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        // O estado é um conjunto FECHADO: a UI nunca precisa adivinhar um valor novo.
        Assert.Equal(
            ["accepted", "running", "completed", "failed", "cancelled", "scopeconflict", "rejected"],
            payload.GetProperty("properties").GetProperty("state").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void TheCriticVerdictIsAClosedSetAndFindingsAreSeverityTyped()
    {
        // Default-FAIL exige um conjunto fechado: um veredito desconhecido não pode ser
        // interpretado como aprovação.
        using var openApi = Load("docs/contracts/openapi.json");
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");

        var response = schemas.GetProperty("CriticReviewResponse").GetProperty("properties");
        Assert.True(response.TryGetProperty("verdict", out _));
        Assert.True(response.TryGetProperty("approved", out _));
        Assert.True(response.TryGetProperty("reasonCode", out _));
        Assert.True(response.TryGetProperty("findings", out _));

        var finding = schemas.GetProperty("CriticFindingContract").GetProperty("properties");
        Assert.True(finding.TryGetProperty("severity", out _));
        Assert.True(finding.TryGetProperty("code", out _));
    }

    [Fact]
    public void TheStartContractDoesNotAcceptClientSuppliedPathScopes()
    {
        // O escopo pertence ao PAPEL. Se o schema publicasse `scopeClaims`, um cliente
        // poderia pedir o próprio escopo — exatamente o bypass que CA-1 fechou.
        using var openApi = Load("docs/contracts/openapi.json");
        var schema = openApi.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("StartAgentRunApiRequest")
            .GetProperty("properties");

        Assert.False(schema.TryGetProperty("scopeClaims", out _));
        Assert.True(schema.TryGetProperty("role", out _));
        Assert.True(schema.TryGetProperty("account", out _));
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
