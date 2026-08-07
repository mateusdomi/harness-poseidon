using System.Text.Json;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Providers.Application;
using Harness.Modules.Providers.Contracts;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Providers;

namespace Harness.Host.Agents;

/// <summary>
/// Ponte de composição entre Providers e Agents. O collector atualiza o Capacity Manager com
/// fatos persistidos; o AgentAccountScheduler continua sendo a única autoridade de conta; por
/// fim o Model Router materializa modelo/provedor e a decisão entra no ledger append-only.
/// </summary>
public sealed class ProviderRoutingCoordinator(
    AgentAccountRegistry accounts,
    ProviderQuotaCollector quotaCollector,
    CapacityManager capacity,
    IModelInvocationStore invocations,
    IAuditEventStore audit)
{
    public async Task RefreshCapacityAsync(
        string tenantId,
        IReadOnlyList<ChiefCard> cards,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var card in cards.DistinctBy(value => value.TaskId))
        {
            var recent = await invocations.GetTaskInvocationsAsync(
                tenantId, card.TaskId, cancellationToken);
            foreach (var account in accounts.List())
            {
                var snapshot = quotaCollector.Collect(
                    account.Alias,
                    account.ProviderKind,
                    recent,
                    now,
                    capacity.GetQuotaSnapshot(account.Alias, now));
                capacity.UpdateQuotaSnapshot(account.Alias, snapshot);
            }
        }
    }

    public async Task<ModelRoutingDecision> RouteAndAuditAsync(
        string tenantId,
        string projectId,
        ChiefDispatch dispatch,
        string? preferredModel,
        string? preferredModelProviderKind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var specs = accounts.List()
            .Select(account => new SimpleAccountSpec(
                account.Alias,
                account.ProviderKind,
                account.AllowedRoles,
                account.AllowedPathScopes,
                account.ConcurrencyLimit,
                account.ActiveAttempts,
                account.Priority))
            .ToArray();
        var selection = new ScheduledAccountSelection(
            dispatch.Selection.SelectedAlias,
            dispatch.Selection.ReasonCode,
            dispatch.Selection.Candidates
                .Select(candidate => new ScheduledAccountCandidate(
                    candidate.Alias,
                    candidate.Eligible,
                    candidate.ReasonCode,
                    candidate.Priority))
                .ToArray(),
            dispatch.Selection.FallbackAliases);
        var decision = ModelRouter.Route(
            specs,
            selection,
            new ModelRoutingRequest(
                dispatch.Card.Role,
                dispatch.Card.RequiredCapability,
                preferredModel,
                null,
                null,
                false,
                dispatch.Card.ScopeClaims,
                now,
                preferredModelProviderKind));

        if (!string.Equals(
                decision.SelectedAlias,
                dispatch.AccountAlias,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "model_router.scheduler_selection_mismatch");
        }

        await audit.AppendAsync(
            new AuditEventAppendCommand(
                tenantId,
                "system",
                "chief-backlog-loop",
                "model.routing_decided",
                "task",
                dispatch.Card.TaskId,
                JsonSerializer.Serialize(new
                {
                    projectId,
                    accountAlias = decision.SelectedAlias,
                    provider = decision.Provider,
                    model = decision.SelectedModel,
                    isFallback = decision.IsFallback,
                    decisionReason = decision.DecisionReason,
                    schedulerReason = dispatch.Selection.ReasonCode,
                    candidates = decision.EvaluatedCandidates,
                }),
                now),
            cancellationToken);

        return decision;
    }
}
