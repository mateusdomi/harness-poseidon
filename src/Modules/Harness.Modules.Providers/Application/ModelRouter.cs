using Harness.Modules.Providers.Contracts;

namespace Harness.Modules.Providers.Application;

/// <summary>
/// Roteador determinístico de modelos (Fase 3 / N4).
///
/// A seleção de CONTA pertence exclusivamente ao AgentAccountScheduler, que conhece adapter,
/// autenticação, capacidade, escopo, concorrência e actor/critic. Este componente recebe essa
/// decisão fechada e materializa somente provedor/modelo e a explicação auditável; assim não
/// existe um segundo scheduler mais permissivo no módulo de Providers.
/// </summary>
public static class ModelRouter
{
    /// <summary>
    /// Materializa o roteamento a partir da seleção autoritativa do scheduler.
    /// </summary>
    public static ModelRoutingDecision Route(
        IReadOnlyList<SimpleAccountSpec> registeredAccounts,
        ScheduledAccountSelection selection,
        ModelRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(registeredAccounts);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);

        var duplicateAlias = registeredAccounts
            .GroupBy(account => account.Alias, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAlias is not null)
        {
            throw new DuplicateRegisteredAccountAliasException(duplicateAlias.Key);
        }

        var evaluated = selection.Candidates
            .Select(candidate => $"{candidate.Alias}:{candidate.ReasonCode}")
            .ToArray();
        if (selection.SelectedAlias is not { Length: > 0 } selectedAlias)
        {
            return new ModelRoutingDecision(
                SelectedAlias: null,
                SelectedModel: null,
                Provider: null,
                IsFallback: false,
                DecisionReason: selection.ReasonCode,
                EvaluatedCandidates: evaluated,
                RoutedAt: request.Now);
        }

        var selected = registeredAccounts.FirstOrDefault(account =>
            string.Equals(account.Alias, selectedAlias, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return new ModelRoutingDecision(
                SelectedAlias: null,
                SelectedModel: null,
                Provider: null,
                IsFallback: false,
                DecisionReason: "model_router.scheduler_selection_not_registered",
                EvaluatedCandidates: evaluated,
                RoutedAt: request.Now);
        }

        var preferredConfigured = selection.Candidates
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Alias, StringComparer.Ordinal)
            .FirstOrDefault();
        var isFallback = preferredConfigured is not null &&
            !string.Equals(selected.Alias, preferredConfigured.Alias, StringComparison.OrdinalIgnoreCase);

        // INC-EVAL-006 (defesa em profundidade): um modelo cujo provider declarado diverge do
        // provider da conta selecionada nunca é ecoado — é semanticamente impossível a CLI de
        // um provider aceitar o identificador de modelo de outro. O modelo cai para o default
        // da conta (null) e a decisão declara o descarte. Provider desconhecido (null) não
        // bloqueia: nulo significa desconhecido, e o guard semântico vive no chamador.
        var crossProviderModel = request.PreferredModel is { Length: > 0 } &&
            request.PreferredModelProviderKind is { Length: > 0 } modelProviderKind &&
            !string.Equals(modelProviderKind, selected.ProviderKind, StringComparison.OrdinalIgnoreCase);

        return new ModelRoutingDecision(
            SelectedAlias: selected.Alias,
            SelectedModel: crossProviderModel ? null : request.PreferredModel,
            Provider: selected.ProviderKind,
            IsFallback: isFallback,
            DecisionReason: crossProviderModel
                ? "model_router.cross_provider_model_dropped"
                : isFallback ? "model_router.routed_to_fallback" : "model_router.routed_to_primary",
            EvaluatedCandidates: evaluated,
            RoutedAt: request.Now);
    }
}
