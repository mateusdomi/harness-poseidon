using Harness.Modules.Readiness.Application;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.Workflows;

namespace Harness.Host.Readiness;

/// <summary>
/// Coleta somente-leitura dos sinais reais das dependências do golden path e delega a
/// avaliação ao <see cref="ReadinessEvaluator"/> puro (ADR-017). Não persiste estado nem
/// cria autoridade de domínio.
/// </summary>
public sealed class ProjectReadinessService(
    IOrganizationStore organizations,
    IProviderCatalogStore providers,
    IAgentCatalogStore agents,
    IWorkflowCatalogStore workflows,
    Agents.AgentRunSettings settings)
{
    // ULIDs do auto-seed de conveniência da RC3 (ADR-018). Enquanto o seed existir (até a
    // Fatia B removê-lo), esses recursos são marcados como Simulated para que a prontidão
    // nunca os apresente como configuração real do usuário. Removidos com a Fatia B.
    private static readonly HashSet<string> SimulatedAccountIds = new(StringComparer.Ordinal)
    {
        "01ARZ3NDEKTSV4RRFFQ69G5FH1",
        "01ARZ3NDEKTSV4RRFFQ69G5FH2",
    };

    private static readonly HashSet<string> SimulatedModelIds = new(StringComparer.Ordinal)
    {
        "01ARZ3NDEKTSV4RRFFQ69G5FJ1",
        "01ARZ3NDEKTSV4RRFFQ69G5FJ2",
        "01ARZ3NDEKTSV4RRFFQ69G5FJ3",
        "01ARZ3NDEKTSV4RRFFQ69G5FJ4",
    };

    /// <summary>Read model de prontidão de um projeto existente. `profileReady` vem da sessão.</summary>
    public async Task<ProjectReadinessSnapshot> EvaluateAsync(
        string tenantId,
        ProjectRecord project,
        bool profileReady,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var organizationReady = (await organizations.ListAsync(tenantId, null, 1, cancellationToken)).Count > 0;

        var accounts = await providers.ListAccountsAsync(tenantId, null, 200, cancellationToken);
        var activeAccount = accounts.FirstOrDefault(account => account.State == "active");
        var accountFact = activeAccount is null
            ? DependencyFact.Missing
            : new DependencyFact(true, SimulatedAccountIds.Contains(activeAccount.Id), activeAccount.Id);

        HashSet<string> activeProviderIds = accounts
            .Where(account => account.State == "active")
            .Select(account => account.ProviderId)
            .ToHashSet(StringComparer.Ordinal);
        var models = await providers.ListModelsAsync(tenantId, null, 200, cancellationToken);
        var chatModel = models.FirstOrDefault(model =>
            model.Enabled &&
            model.Capabilities.Contains("chat", StringComparer.Ordinal) &&
            (model.EffortMappings ?? []).Count > 0 &&
            activeProviderIds.Contains(model.ProviderId));
        var modelFact = chatModel is null
            ? DependencyFact.Missing
            : new DependencyFact(true, SimulatedModelIds.Contains(chatModel.Id), chatModel.Id);

        var workflowBound = (await workflows.ListBindingsAsync(tenantId, project.Id, null, 1, cancellationToken)).Count > 0;

        var chiefFact = await ResolveChiefAsync(tenantId, project.ChiefAgentId, activeProviderIds, cancellationToken);

        // Sinais OPERACIONAIS: sem eles a prontidão dizia "pronto" para um projeto que nunca ia
        // andar. A esteira desligada e o repositório inacessível produzem exatamente o mesmo
        // sintoma para quem pediu o projeto — silêncio — e nenhum dos dois aparecia em lugar
        // nenhum da experiência.
        var repositoryReachable = RepositoryIsReachable(project.RepositoryUrl);

        var inputs = new ReadinessInputs(
            profileReady,
            organizationReady,
            ProjectExists: true,
            project.Id,
            accountFact,
            modelFact,
            workflowBound,
            chiefFact,
            settings.AutoDispatchEnabled,
            repositoryReachable);
        return ReadinessEvaluator.Evaluate(inputs);
    }

    /// <summary>
    /// A pasta de trabalho DECLARADA pelo projeto existe. O alvo é o defeito silencioso observado:
    /// o projeto aponta para um caminho local que sumiu (movido, renomeado, em outro disco), toda
    /// tentativa morre na largada e a causa só aparece no log de execução.
    ///
    /// O que NÃO se afirma aqui: um caminho remoto não é verificado (exigiria IO de rede) e a
    /// ausência de repositório declarado não é tratada como falha — sobre ela esta checagem não
    /// tem prova, e afirmar sem prova é o mesmo erro que ela existe para corrigir.
    /// </summary>
    private static bool RepositoryIsReachable(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl) ||
            repositoryUrl.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return Directory.Exists(Path.GetFullPath(repositoryUrl));
        }
        catch (Exception exception) when (
            exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<ChiefFact> ResolveChiefAsync(
        string tenantId,
        string chiefAgentId,
        HashSet<string> activeProviderIds,
        CancellationToken cancellationToken)
    {
        var agent = await agents.GetAgentAsync(tenantId, chiefAgentId, cancellationToken);
        if (agent is null)
        {
            return ChiefFact.Missing;
        }

        var definition = await agents.GetDefinitionForTenantAsync(tenantId, agent.DefinitionId, cancellationToken);
        var modelId = agent.ModelId ?? definition?.DefaultModelId;
        var accountId = agent.AccountId ?? definition?.PreferredAccountId;
        var healthy = agent.State is not "error";

        if (modelId is null)
        {
            return new ChiefFact(AgentPresent: true, healthy, ModelResolves: false, Simulated: false, chiefAgentId);
        }

        var model = await providers.GetModelAsync(tenantId, modelId, cancellationToken);
        var modelResolves = model is not null &&
            model.Enabled &&
            model.Capabilities.Contains("chat", StringComparer.Ordinal) &&
            activeProviderIds.Contains(model.ProviderId);
        var simulated = SimulatedModelIds.Contains(modelId) ||
            (accountId is not null && SimulatedAccountIds.Contains(accountId));
        return new ChiefFact(AgentPresent: true, healthy, modelResolves, simulated, chiefAgentId);
    }
}
