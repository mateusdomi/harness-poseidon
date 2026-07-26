using Harness.Modules.Providers.Contracts;

namespace Harness.Modules.Providers.Application;

/// <summary>
/// Roteador determinístico de modelos com suporte a fallback, circuit breaker e backpressure (Fase 3 / N4).
/// Seleciona a melhor conta e modelo para cada requisição garantindo preservação integral do contexto.
/// </summary>
public sealed class ModelRouter
{
    private readonly CapacityManager _capacityManager;

    public ModelRouter(CapacityManager capacityManager)
    {
        _capacityManager = capacityManager ?? throw new ArgumentNullException(nameof(capacityManager));
    }

    /// <summary>
    /// Realiza o roteamento determinístico da requisição para um modelo e conta elegível.
    /// </summary>
    public ModelRoutingDecision Route(
        IReadOnlyList<SimpleAccountSpec> registeredAccounts,
        ModelRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(registeredAccounts);
        ArgumentNullException.ThrowIfNull(request);

        var capacitySnapshot = _capacityManager.EvaluateCapacity(
            registeredAccounts, request.Role, request.Now);

        var candidates = registeredAccounts
            .Where(acc => acc.AllowedRoles.Contains(request.Role, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(acc => acc.Priority)
            .ThenBy(acc => acc.Alias, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            return new ModelRoutingDecision(
                SelectedAlias: null,
                SelectedModel: null,
                Provider: null,
                IsFallback: false,
                DecisionReason: "model_router.no_accounts_configured_for_role",
                EvaluatedCandidates: [],
                RoutedAt: request.Now);
        }

        var evaluatedLogs = new List<string>();
        SimpleAccountSpec? selected = null;

        foreach (var candidate in candidates)
        {
            var isTripped = _capacityManager.IsCircuitTripped(candidate.Alias, request.Now);
            var quota = _capacityManager.GetQuotaSnapshot(candidate.Alias, request.Now);
            var isExhausted = string.Equals(quota.Status, "Exhausted", StringComparison.OrdinalIgnoreCase) &&
                              (quota.ResetAt is null || quota.ResetAt.Value > request.Now);

            if (isTripped)
            {
                evaluatedLogs.Add($"{candidate.Alias}:circuit_tripped");
                continue;
            }

            if (isExhausted)
            {
                evaluatedLogs.Add($"{candidate.Alias}:quota_exhausted");
                continue;
            }

            if (request.ForCritic && request.ActorAlias is not null &&
                string.Equals(candidate.Alias, request.ActorAlias, StringComparison.OrdinalIgnoreCase))
            {
                evaluatedLogs.Add($"{candidate.Alias}:actor_cannot_be_critic");
                continue;
            }

            evaluatedLogs.Add($"{candidate.Alias}:eligible");
            selected = candidate;
            break;
        }

        if (selected is null)
        {
            var reason = capacitySnapshot.IsUnderBackpressure
                ? "model_router.backpressure_all_accounts_exhausted"
                : "model_router.no_eligible_candidate";

            return new ModelRoutingDecision(
                SelectedAlias: null,
                SelectedModel: null,
                Provider: null,
                IsFallback: false,
                DecisionReason: reason,
                EvaluatedCandidates: evaluatedLogs,
                RoutedAt: request.Now);
        }

        var topCandidate = candidates.FirstOrDefault();
        var isFallback = topCandidate is not null && !string.Equals(selected.Alias, topCandidate.Alias, StringComparison.OrdinalIgnoreCase);

        var model = request.PreferredModel ?? "default";

        return new ModelRoutingDecision(
            SelectedAlias: selected.Alias,
            SelectedModel: model,
            Provider: selected.ProviderKind,
            IsFallback: isFallback,
            DecisionReason: isFallback ? "model_router.routed_to_fallback" : "model_router.routed_to_primary",
            EvaluatedCandidates: evaluatedLogs,
            RoutedAt: request.Now);
    }
}
