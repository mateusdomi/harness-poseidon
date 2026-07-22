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

        // Providers: o catálogo lazy por tenant oferece apenas os TIPOS conectáveis; contas,
        // modelos, cotas e roteamento não são semeados em instalação normal (ADR-018). O
        // cenário pede explicitamente o catálogo SIMULADO antes de exercer essas leituras.
        var providerRows = await providers.ListProvidersAsync(tenantId, null, 50, cancellationToken);
        Assert.NotEmpty(providerRows);
        Assert.Empty(await providers.ListAccountsAsync(tenantId, null, 50, cancellationToken));
        Assert.Empty(await providers.ListModelsAsync(tenantId, null, 50, cancellationToken));
        Assert.Empty(await providers.ListBudgetsAsync(tenantId, null, 50, cancellationToken));
        await providers.SeedSimulatedCatalogAsync(tenantId, cancellationToken);
        var models = await providers.ListModelsAsync(tenantId, null, 50, cancellationToken);
        Assert.NotEmpty(models);
        Assert.All(models, model => Assert.Equal(["low", "medium", "high", "max"], (model.EffortMappings ?? []).Select(value => value.Effort)));
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

        const string disposableAccountId = "01ARZ3NDEKTSV4RRFFQ69G5FN1";
        var created = await providers.CreateAccountAsync(
            new ProviderAccountCreateCommand(
                tenantId, tenantId, disposableAccountId, providerRows[0].Id,
                "Provider-neutral disposable", "secret://providers/disposable", 50m,
                DateTimeOffset.Parse("2026-07-19T12:01:00Z", System.Globalization.CultureInfo.InvariantCulture),
                "automation@example.test", "team", "oauth", "weekly",
                DateTimeOffset.Parse("2026-07-26T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                ["chat", "code", "tools"]),
            cancellationToken);
        Assert.Equal("disabled", created.State);
        Assert.Equal("automation@example.test", created.Identity);
        Assert.Equal("team", created.Plan);
        Assert.Equal("oauth", created.Authentication);
        Assert.Equal("weekly", created.QuotaWindow);
        Assert.Equal(["chat", "code", "tools"], created.Capabilities);
        await providers.DeleteAccountAsync(
            new ProviderAccountDeleteCommand(
                tenantId, tenantId, disposableAccountId,
                DateTimeOffset.Parse("2026-07-19T12:02:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);
        Assert.Null(await providers.GetAccountAsync(tenantId, disposableAccountId, cancellationToken));

        const string disposableModelId = "01ARZ3NDEKTSV4RRFFQ69G5FN2";
        var createdModel = await providers.CreateModelAsync(new ProviderModelCreateCommand(
            tenantId, tenantId, disposableModelId, providerRows[0].Id, "parity-model",
            "Parity model", ["code", "chat"], 131072, 0.002m, 0.008m,
            [new("high", "provider-high"), new("low", "provider-low")],
            DateTimeOffset.Parse("2026-07-19T12:02:10Z", System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);
        Assert.False(createdModel.Enabled);
        Assert.Equal(["chat", "code"], createdModel.Capabilities);
        Assert.Equal(["low", "high"], createdModel.EffortMappings!.Select(x => x.Effort));
        var updatedModel = Assert.IsType<ModelRecord>(await providers.UpdateAsync(
            new ProviderCatalogUpdateCommand(tenantId, tenantId, "models", disposableModelId,
                "{\"displayName\":\"Parity model v2\",\"enabled\":true,\"capabilities\":[\"embeddings\",\"chat\"],\"contextWindow\":262144,\"costPer1kInputUsd\":0.003,\"costPer1kOutputUsd\":0.009,\"effortMappings\":[{\"effort\":\"medium\",\"providerValue\":\"balanced\"}]}",
                DateTimeOffset.Parse("2026-07-19T12:02:20Z", System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken));
        Assert.Equal("Parity model v2", updatedModel.DisplayName);
        Assert.Equal(262144, updatedModel.ContextWindow);
        Assert.Equal(["chat", "embeddings"], updatedModel.Capabilities);
        Assert.Equal("balanced", Assert.Single(updatedModel.EffortMappings!).ProviderValue);
        await Assert.ThrowsAsync<ProviderCatalogLifecycleException>(() => providers.DeleteModelAsync(
            new ProviderModelDeleteCommand(tenantId, tenantId, disposableModelId,
                DateTimeOffset.Parse("2026-07-19T12:02:30Z", System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken));
        _ = await providers.UpdateAsync(new ProviderCatalogUpdateCommand(
            tenantId, tenantId, "models", disposableModelId, "{\"enabled\":false}",
            DateTimeOffset.Parse("2026-07-19T12:02:40Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        await providers.DeleteModelAsync(new ProviderModelDeleteCommand(
            tenantId, tenantId, disposableModelId,
            DateTimeOffset.Parse("2026-07-19T12:02:50Z", System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);
        Assert.Null(await providers.GetModelAsync(tenantId, disposableModelId, cancellationToken));

        _ = await providers.UpdateAsync(new ProviderCatalogUpdateCommand(
            tenantId, tenantId, "accounts", account.Id, "{\"state\":\"active\"}",
            DateTimeOffset.Parse("2026-07-19T12:03:00Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        var tenantAgents = await agents.ListAgentsAsync(tenantId, null, null, 50, cancellationToken);
        if (tenantAgents.Count > 0)
        {
            var compatibleModels = models.Where(value => value.ProviderId == account.ProviderId && value.Enabled).ToArray();
            var selected = await agents.UpdateSelectionAsync(new AgentSelectionCommand(
                tenantId, tenantAgents[0].Id, tenantId, account.Id, compatibleModels[0].Id,
                "max", [compatibleModels[1].Id], "Provider-neutral selection.",
                DateTimeOffset.Parse("2026-07-19T12:04:00Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
            Assert.Equal("max", selected.Effort);
            Assert.NotNull(selected.ProviderEffortValue);
        }

        var definitionModels = models.Where(value => value.ProviderId == account.ProviderId && value.Enabled).ToArray();
        var definitionContent = new AgentDefinitionContent(
            "provider-neutral-reviewer", "Provider-neutral Reviewer", "specialist", "Review",
            "Reviews provider-neutral behavior.", definitionModels[0].Id, [], [], "Critical reviewer",
            "Protect parity.", ["Compare providers"], ["Parity report"], ["Equivalent result"],
            "Concise", ["No self approval"], ["dotnet", "postgres"], "high", account.Id,
            [definitionModels[1].Id], "Platform", "critic", "high");
        const string customDefinitionId = "01ARZ3NDEKTSV4RRFFQ69G5FP1";
        var customDefinition = await agents.CreateDefinitionAsync(new(
            tenantId, tenantId, customDefinitionId, definitionContent,
            DateTimeOffset.Parse("2026-07-19T12:05:00Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        Assert.Equal(1, customDefinition.Version);
        Assert.Equal(["dotnet", "postgres"], customDefinition.Stacks);
        Assert.Equal("high", customDefinition.DefaultEffort);
        Assert.Equal(account.Id, customDefinition.PreferredAccountId);
        Assert.Equal([definitionModels[1].Id], customDefinition.FallbackModelIds);
        Assert.Equal(("Platform", "critic", "high"),
            (customDefinition.Team, customDefinition.ActorCritic, customDefinition.Risk));
        customDefinition = await agents.UpdateDefinitionAsync(new(
            tenantId, tenantId, customDefinitionId, 1,
            definitionContent with { Name = "Provider-neutral Senior Reviewer" },
            DateTimeOffset.Parse("2026-07-19T12:06:00Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        Assert.Equal(2, customDefinition.Version);
        var latestDefinitionVersion = Assert.Single(await agents.ListDefinitionVersionsAsync(
            tenantId, customDefinitionId, null, 1, cancellationToken));
        Assert.Equal(2, latestDefinitionVersion.Version);
        Assert.Equal("Provider-neutral Senior Reviewer", latestDefinitionVersion.Snapshot.Name);
        Assert.Equal(["dotnet", "postgres"], latestDefinitionVersion.Snapshot.Stacks);
        Assert.Equal(account.Id, latestDefinitionVersion.Snapshot.PreferredAccountId);
        var initialDefinitionVersion = Assert.Single(await agents.ListDefinitionVersionsAsync(
            tenantId, customDefinitionId, 2, 1, cancellationToken));
        Assert.Equal(1, initialDefinitionVersion.Version);
        Assert.Equal("Provider-neutral Reviewer", initialDefinitionVersion.Snapshot.Name);
        const string duplicateDefinitionId = "01ARZ3NDEKTSV4RRFFQ69G5FP2";
        var duplicate = await agents.DuplicateDefinitionAsync(new(
            tenantId, tenantId, customDefinitionId, duplicateDefinitionId,
            "provider-neutral-reviewer-copy", "Provider-neutral Reviewer Copy",
            DateTimeOffset.Parse("2026-07-19T12:07:00Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        Assert.Equal(1, duplicate.Version);

        // Auto-key P1 end-to-end: a MESMA composição do endpoint (listar chaves do tenant →
        // gerar chave versionada → criar) contra o store real. A base `provider-neutral-reviewer`
        // já existe, então a chave gerada é versionada, e o store a aceita.
        var existingKeys = (await agents.ListDefinitionsForTenantAsync(
                tenantId, null, 500, includeArchived: true, cancellationToken))
            .Select(definition => definition.Key)
            .ToArray();
        Assert.Contains("provider-neutral-reviewer", existingKeys);
        var autoKey = AgentKeyGenerator.Generate("Provider-neutral Reviewer", existingKeys);
        Assert.Equal("provider-neutral-reviewer-2", autoKey);
        const string autoKeyDefinitionId = "01ARZ3NDEKTSV4RRFFQ69G5FP3";
        var autoKeyed = await agents.CreateDefinitionAsync(new(
            tenantId, tenantId, autoKeyDefinitionId,
            definitionContent with { Key = autoKey, Name = "Provider-neutral Reviewer" },
            DateTimeOffset.Parse("2026-07-19T12:07:30Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        Assert.Equal("provider-neutral-reviewer-2", autoKeyed.Key);
        Assert.Equal(1, autoKeyed.Version);
        await agents.DeleteDefinitionAsync(new(
            tenantId, tenantId, autoKeyDefinitionId,
            DateTimeOffset.Parse("2026-07-19T12:07:45Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);

        await agents.DeleteDefinitionAsync(new(
            tenantId, tenantId, duplicateDefinitionId,
            DateTimeOffset.Parse("2026-07-19T12:08:00Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        customDefinition = await agents.SetDefinitionLifecycleAsync(new(
            tenantId, tenantId, customDefinitionId, "archive",
            DateTimeOffset.Parse("2026-07-19T12:09:00Z", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
        Assert.NotNull(customDefinition.ArchivedAt);
    }
}
