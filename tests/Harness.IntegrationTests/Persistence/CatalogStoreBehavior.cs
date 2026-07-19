using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.Tools;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// Cenário provider-neutro dos catálogos semeados: definições de agentes,
/// ferramentas/skills/plugins/MCP e o catálogo lazy de providers/budgets devem
/// oferecer os mesmos seeds e leituras em SQLite e PostgreSQL.
/// </summary>
public static class CatalogStoreBehavior
{
    private static readonly string[] CanonicalAgentKeys =
    [
        "chief-orchestrator", "critic-qa", "product-requirements-analyst",
        "software-architect", "software-engineer", "technical-writer",
    ];

    public static async Task AssertAsync(
        IAgentCatalogStore agents,
        IToolCatalogStore tools,
        IProviderCatalogStore providers,
        string tenantId,
        CancellationToken cancellationToken)
    {
        // Definições canônicas de agentes: exatamente as seis da missão.
        var definitions = await agents.ListDefinitionsAsync(null, 50, cancellationToken);
        Assert.Equal(
            CanonicalAgentKeys.Order(StringComparer.Ordinal),
            definitions.Select(definition => definition.Key).Order(StringComparer.Ordinal));
        var chief = definitions.Single(definition => definition.Key == "chief-orchestrator");
        Assert.Equal(chief.Id, (await agents.GetDefinitionAsync(chief.Id, cancellationToken))!.Id);

        // Catálogo de ferramentas semeado e navegável.
        Assert.NotEmpty(await tools.ListSkillsAsync(null, 50, cancellationToken));
        var seededTools = await tools.ListToolsAsync(null, 50, cancellationToken);
        Assert.NotEmpty(seededTools);
        Assert.Equal(
            seededTools[0].Id,
            (await tools.GetToolAsync(seededTools[0].Id, cancellationToken))!.Id);
        Assert.NotEmpty(await tools.ListPluginsAsync(null, 50, cancellationToken));
        Assert.NotEmpty(await tools.ListMcpServersAsync(null, 50, cancellationToken));

        // Providers: catálogo lazy por tenant com budgets semeados (global + contas).
        var providerRows = await providers.ListProvidersAsync(tenantId, null, 50, cancellationToken);
        Assert.NotEmpty(providerRows);
        var models = await providers.ListModelsAsync(tenantId, null, 50, cancellationToken);
        Assert.NotEmpty(models);
        var budgets = await providers.ListBudgetsAsync(tenantId, null, 50, cancellationToken);
        Assert.Contains(budgets, budget => budget.Scope == "global");
        Assert.Contains(budgets, budget => budget.Scope == "account");
        var global = budgets.Single(budget => budget.Scope == "global");
        Assert.Equal(global.Id, (await providers.GetBudgetAsync(tenantId, global.Id, cancellationToken))!.Id);

        var account = Assert.Single(await providers.ListAccountsAsync(tenantId, null, 1, cancellationToken));
        var updated = Assert.IsType<AccountRecord>(await providers.UpdateAsync(
            new ProviderCatalogUpdateCommand(
                tenantId,
                tenantId,
                "accounts",
                account.Id,
                "{\"label\":\"Provider-neutral account\",\"state\":\"disabled\",\"quotaLimitUsd\":125}",
                DateTimeOffset.Parse("2026-07-19T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken));
        Assert.Equal("Provider-neutral account", updated.Label);
        Assert.Equal("disabled", updated.State);
        Assert.Equal(125m, updated.QuotaLimitUsd);
    }
}
