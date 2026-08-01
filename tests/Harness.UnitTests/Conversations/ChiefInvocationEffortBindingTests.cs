using Harness.Host.Conversations;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Conversations.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Providers;

namespace Harness.UnitTests.Conversations;

/// <summary>
/// RN-05 — prova do binding REAL do esforço: o valor escolhido no chat
/// (`StartChatTurnRequest.Effort`) é resolvido pelo roteamento contra os
/// mapeamentos publicados do modelo e propagado como
/// <see cref="ChiefInvocationSelection.ProviderEffortValue"/>, que o worker
/// injeta em <see cref="AgentExecutionRequest.Effort"/> (o `--effort` da CLI).
/// Alto ≠ Médio no valor efetivamente propagado — não é fictício.
/// </summary>
public sealed class ChiefInvocationEffortBindingTests
{
    private const string Tenant = "tenant-1";
    private const string ChiefAgentId = "01ARZ3NDEKTSV4RRFFQ69G5AA1";
    private const string DefinitionId = "01ARZ3NDEKTSV4RRFFQ69G5DD1";
    private const string ModelId = "01ARZ3NDEKTSV4RRFFQ69G5MM1";
    private const string ProviderId = "01ARZ3NDEKTSV4RRFFQ69G5PP1";
    private const string AccountId = "01ARZ3NDEKTSV4RRFFQ69G5CC1";

    // Modelo com valores de provider DISTINTOS por esforço: é justamente o que
    // torna o teste uma prova — se o elo estivesse quebrado, Alto e Médio
    // colapsariam no mesmo valor propagado.
    private static readonly EffortMappingRecord[] Mappings =
    [
        new("low", "low"),
        new("medium", "medium"),
        new("high", "xhigh"),
        new("max", "xhigh"),
    ];

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "xhigh")]
    public async Task ChosenEffortResolvesToItsMappedProviderValue(string effort, string providerValue)
    {
        var routing = new ChiefInvocationRoutingService(new FakeAgents(), new FakeProviders());

        var selection = await routing.ResolveAsync(
            Tenant, ChiefAgentId,
            new StartChatTurnRequest("Execute.", Effort: effort),
            CancellationToken.None);

        Assert.Equal(effort, selection.Effort);
        // O valor propagado ao executor é o do provider, não o rótulo canônico.
        Assert.Equal(providerValue, selection.ProviderEffortValue);
    }

    [Fact]
    public async Task HighPropagatesADifferentProviderValueThanMedium()
    {
        var routing = new ChiefInvocationRoutingService(new FakeAgents(), new FakeProviders());

        var medium = await routing.ResolveAsync(
            Tenant, ChiefAgentId, new StartChatTurnRequest("Execute.", Effort: "medium"),
            CancellationToken.None);
        var high = await routing.ResolveAsync(
            Tenant, ChiefAgentId, new StartChatTurnRequest("Execute.", Effort: "high"),
            CancellationToken.None);

        // O elo que o worker consome (ChiefTurnBackgroundService monta
        // AgentExecutionRequest.Effort = Selection.ProviderEffortValue).
        Assert.NotEqual(medium.ProviderEffortValue, high.ProviderEffortValue);
        Assert.Equal("medium", medium.ProviderEffortValue);
        Assert.Equal("xhigh", high.ProviderEffortValue);

        // Prova de que o valor propagado chega ao contrato do executor sem se perder.
        var request = new AgentExecutionRequest(
            Tenant, "project", "conversation", ChiefAgentId, "instruction", "{}", "/tmp",
            Model: high.ModelName, Effort: high.ProviderEffortValue);
        Assert.Equal("xhigh", request.Effort);
    }

    private sealed class FakeAgents : IAgentCatalogStore
    {
        public Task<AgentRecord?> GetAgentAsync(string tenantId, string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentRecord?>(new AgentRecord(
                Tenant, ChiefAgentId, DefinitionId, null, "Chief", "active", null, ModelId, null,
                new AgentMetricsRecord(0, 0, 0, 0m, 0), null, AccountId));

        public Task<AgentDefinitionRecord?> GetDefinitionForTenantAsync(string tenantId, string definitionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentDefinitionRecord?>(new AgentDefinitionRecord(
                DefinitionId, "chief-orchestrator", "Chief", "chief", null, "desc", ModelId, [], []));

        public Task<AgentDefinitionRecord?> GetDefinitionAsync(string definitionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsAsync(string? afterId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentRecord>> ListAgentsAsync(string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRecord> UpdateSelectionAsync(AgentSelectionCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<(AgentRecord Agent, bool Created)> EnsureProjectAgentAsync(ProjectAgentEnsureCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentDefinitionRecord>> ListDefinitionsForTenantAsync(string tenantId, string? afterId, int limit, bool includeArchived, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentDefinitionVersionRecord>> ListDefinitionVersionsAsync(string tenantId, string definitionId, int? beforeVersion, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentDefinitionRecord> CreateDefinitionAsync(AgentDefinitionCreateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentDefinitionRecord> UpdateDefinitionAsync(AgentDefinitionUpdateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentDefinitionRecord> DuplicateDefinitionAsync(AgentDefinitionDuplicateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentDefinitionRecord> SetDefinitionLifecycleAsync(AgentDefinitionLifecycleCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteDefinitionAsync(AgentDefinitionDeleteCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> EnsureBuiltInDefinitionsAsync(IReadOnlyList<BuiltInAgentDefinitionSeed> definitions, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<(AgentDefinitionRecord Definition, bool Created)> CreateChiefDefinitionAsync(ChiefDefinitionCreateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentDefinitionRecord> SetDefinitionLifecycleStateAsync(AgentDefinitionLifecycleStateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeProviders : IProviderCatalogStore
    {
        public Task<ModelRecord?> GetModelAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelRecord?>(new ModelRecord(
                ModelId, ProviderId, "test-model", "Test Model", ["chat", "code"], 200000,
                0.001m, 0.002m, true, Mappings));

        public Task<AccountRecord?> GetAccountAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountRecord?>(new AccountRecord(
                AccountId, ProviderId, "Account", "active", null, 0m));

        public Task<IReadOnlyList<AccountRecord>> ListAccountsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountRecord>>(
                [new AccountRecord(AccountId, ProviderId, "Account", "active", null, 0m)]);

        public Task<IReadOnlyList<ProviderRecord>> ListProvidersAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ProviderRecord?> GetProviderAsync(string tenantId, string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AccountRecord> CreateAccountAsync(ProviderAccountCreateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAccountAsync(ProviderAccountDeleteCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ModelRecord>> ListModelsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ModelRecord> CreateModelAsync(ProviderModelCreateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteModelAsync(ProviderModelDeleteCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RoutingPolicyRecord>> ListRoutingPoliciesAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RoutingPolicyRecord?> GetRoutingPolicyAsync(string tenantId, string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<BudgetRecord>> ListBudgetsAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BudgetRecord?> GetBudgetAsync(string tenantId, string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ProviderCatalogRecord> UpdateAsync(ProviderCatalogUpdateCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ModelRecord>> SyncAsync(ProviderCatalogSyncCommand command, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SeedSimulatedCatalogAsync(string tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
