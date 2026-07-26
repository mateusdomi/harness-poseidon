using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Um CARD candidato a despacho — a unidade AUDITÁVEL de delegação do chefe (uma tarefa
/// `Ready` do board, com a instrução que vai ao agente). O card carrega o papel lógico e o
/// escopo do trabalho; a conta que o executa é escolhida pela política, não fixada no card.
/// </summary>
public sealed record ChiefCard(
    string TaskId,
    string ProjectId,
    string Role,
    string RequiredCapability,
    int Priority,
    IReadOnlyList<string> ScopeClaims);

/// <summary>Decisão de despachar um card para uma conta disponível, com o motivo explicável.</summary>
public sealed record ChiefDispatch(ChiefCard Card, string AccountAlias, string ReasonCode);

/// <summary>
/// Card adiado: nenhuma conta do papel está disponível AGORA. <see cref="RetryAfter"/> é
/// quando a conta mais próxima volta (do ledger de cota), para o agendador retomar — nulo
/// quando o bloqueio exige ação humana (login) ou não há conta do papel.
/// </summary>
public sealed record ChiefDeferral(ChiefCard Card, string ReasonCode, DateTimeOffset? RetryAfter);

/// <summary>Plano de uma rodada do chefe: o que despachar agora e o que ficou adiado (e por quê).</summary>
public sealed record ChiefBacklogPlan(
    IReadOnlyList<ChiefDispatch> Dispatch,
    IReadOnlyList<ChiefDeferral> Deferred);

/// <summary>
/// O "cérebro" do despacho do chefe (loop de backlog): dado o backlog de cards e a
/// disponibilidade REAL das contas (cota/cooldown do ledger durável), decide QUAL card vai
/// para QUAL conta AGORA — por prioridade, papel, disponibilidade e concorrência — e o que
/// fica adiado com o motivo tipado. É PURO e explicável: é aqui que o operador ajusta o
/// comportamento de planejamento do chefe e audita cada decisão.
/// </summary>
public sealed class ChiefBacklogPolicy(AgentAccountScheduler? scheduler = null)
{
    private readonly AgentAccountScheduler _scheduler = scheduler ?? new AgentAccountScheduler();

    /// <param name="capacitySignals">
    /// Sinais de capacidade externos ao ledger (circuit breaker e backpressure do Capacity
    /// Manager, Fase 3), por alias. São tratados como cota indisponível: um circuito aberto
    /// nunca recebe card e a janela de volta alimenta o `RetryAfter` do adiamento. O módulo de
    /// provedores não é dependência daqui — quem compõe o sinal é o Host.
    /// </param>
    public ChiefBacklogPlan Plan(
        IReadOnlyList<ChiefCard> backlog,
        AgentAccountRegistry accounts,
        AccountAvailabilityLedger availability,
        int maxConcurrentDispatch,
        DateTimeOffset now,
        IReadOnlyDictionary<string, AccountQuotaSnapshot>? capacitySignals = null)
    {
        ArgumentNullException.ThrowIfNull(backlog);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(availability);

        // Cota/cooldown do ledger vira sinal para o scheduler: uma conta com janela ativa é
        // tratada como esgotada e nunca é escolhida — sem inventar percentual, só a data de volta.
        var quotas = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in availability.List())
        {
            if (record.CooldownUntil is { } until && until > now &&
                record.State is AgentAccountState.QuotaLimited or AgentAccountState.CoolingDown)
            {
                quotas[record.Alias] = new AccountQuotaSnapshot(
                    "availability-ledger", record.UpdatedAt, QuotaStatus.Exhausted,
                    QuotaConfidence.High, 0, until, TimeSpan.FromMinutes(1));
            }
        }

        // O sinal de capacidade só APERTA a decisão: entra quando indica indisponibilidade
        // ativa e nunca reabre uma conta que o ledger já bloqueou.
        foreach (var (alias, signal) in capacitySignals ?? new Dictionary<string, AccountQuotaSnapshot>())
        {
            if (signal.IsExhaustedAt(now) && !quotas.ContainsKey(alias))
            {
                quotas[alias] = signal;
            }
        }

        var dispatched = new List<ChiefDispatch>();
        var deferred = new List<ChiefDeferral>();

        // Orçamento de slots por conta NESTA rodada (o scheduler não sabe do que já alocamos aqui).
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var limit = Math.Max(0, maxConcurrentDispatch);

        foreach (var card in backlog.OrderByDescending(entry => entry.Priority))
        {
            if (dispatched.Count >= limit)
            {
                deferred.Add(new ChiefDeferral(card, "chief.dispatch_budget_reached", null));
                continue;
            }

            var request = new AccountSchedulingRequest
            {
                Role = card.Role,
                RequiredCapability = card.RequiredCapability,
                Now = now,
                RequiredPathScopes = card.ScopeClaims,
                Quotas = quotas,
            };

            var decision = _scheduler.Select(accounts, request);
            var chosen = FirstFreeSlot(decision, accounts, used);
            if (chosen is null)
            {
                var retryAfter = NextReturn(decision, quotas);
                deferred.Add(new ChiefDeferral(
                    card,
                    retryAfter is null ? decision.ReasonCode : "chief.awaiting_account_return",
                    retryAfter));
                continue;
            }

            used[chosen] = used.GetValueOrDefault(chosen) + 1;
            dispatched.Add(new ChiefDispatch(card, chosen, "chief.dispatched"));
        }

        return new ChiefBacklogPlan(dispatched, deferred);
    }

    /// <summary>
    /// A conta preferida (ou o primeiro fallback) que AINDA tem slot livre nesta rodada,
    /// considerando os despachos já alocados aqui e o limite de concorrência da conta.
    /// </summary>
    private static string? FirstFreeSlot(
        AccountSelectionDecision decision,
        AgentAccountRegistry accounts,
        Dictionary<string, int> used)
    {
        foreach (var alias in Enumerate(decision))
        {
            var account = accounts.Get(alias);
            if (account is null)
            {
                continue;
            }

            var available = account.ConcurrencyLimit - account.ActiveAttempts - used.GetValueOrDefault(alias);
            if (available > 0)
            {
                return alias;
            }
        }

        return null;
    }

    private static IEnumerable<string> Enumerate(AccountSelectionDecision decision)
    {
        if (decision.SelectedAlias is { Length: > 0 } selected)
        {
            yield return selected;
        }

        foreach (var fallback in decision.FallbackAliases)
        {
            yield return fallback;
        }
    }

    /// <summary>Quando a conta elegível mais próxima volta (menor cooldown entre as candidatas).</summary>
    private static DateTimeOffset? NextReturn(
        AccountSelectionDecision decision,
        Dictionary<string, AccountQuotaSnapshot> quotas)
    {
        DateTimeOffset? soonest = null;
        foreach (var candidate in decision.Candidates)
        {
            if (quotas.TryGetValue(candidate.Alias, out var quota) && quota.ResetAt is { } reset &&
                (soonest is null || reset < soonest))
            {
                soonest = reset;
            }
        }

        return soonest;
    }
}
