using System.Text.Json;

namespace Harness.ContractTests.Providers;

public sealed class ProviderContractDriftTests
{
    [Theory]
    [InlineData("providers", "ProviderContract", "providerSchema", "Provider", "id,kind,name,baseUrl,enabled", true)]
    [InlineData("accounts", "AccountContract", "accountSchema", "Account", "id,providerId,label,state,quotaLimitUsd,quotaUsedUsd,identity,plan,authentication,health,quotaWindow,quotaResetsAt,capabilities", true)]
    [InlineData("models", "ModelContract", "modelSchema", "Model", "id,providerId,name,displayName,capabilities,contextWindow,costPer1kInputUsd,costPer1kOutputUsd,enabled,effortMappings", true)]
    [InlineData("routing-policies", "RoutingPolicyContract", "routingPolicySchema", "RoutingPolicy", "id,projectId,name,rules,active", true)]
    [InlineData("budgets", "BudgetContract", "budgetSchema", "Budget", "id,scope,scopeId,period,limitUsd,spentUsd,alertThresholdPct", true)]
    public void OpenApiMatchesProviderFrontendContracts(string route, string schema, string marker, string type, string csv, bool patch)
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "openapi.json")));
        var api = document.RootElement; var paths = api.GetProperty("paths"); Assert.True(paths.GetProperty($"/api/v1/{route}").TryGetProperty("get", out _));
        var item = paths.GetProperty($"/api/v1/{route}/{{id}}"); Assert.True(item.TryGetProperty("get", out _)); Assert.Equal(patch, item.TryGetProperty("patch", out _));
        if (route == "accounts")
        {
            Assert.True(paths.GetProperty("/api/v1/accounts").TryGetProperty("post", out _));
            Assert.True(item.TryGetProperty("delete", out _));
        }
        var fields = csv.Split(','); var actual = api.GetProperty("components").GetProperty("schemas").GetProperty(schema).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(fields.Order(StringComparer.Ordinal), actual);
        var source = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "providers.ts")); var start = source.IndexOf($"export const {marker}", StringComparison.Ordinal); var end = source.IndexOf($"export type {type}", start, StringComparison.Ordinal); Assert.True(start >= 0 && end > start); var block = source[start..end]; var frontendFields = route switch { "accounts" => fields.Take(6), "models" => fields.Take(9), _ => fields }; Assert.All(frontendFields, f => Assert.Contains($"{f}:", block, StringComparison.Ordinal));
        if (route == "providers") Assert.True(paths.GetProperty("/api/v1/providers/{id}/sync").TryGetProperty("post", out _));
    }
    private static string FindRepositoryRoot() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "Harness.sln"))) d = d.Parent; return d?.FullName ?? throw new DirectoryNotFoundException(); }
}
