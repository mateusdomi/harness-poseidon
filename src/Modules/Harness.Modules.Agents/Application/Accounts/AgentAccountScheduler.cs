using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;

namespace Harness.Modules.Agents.Application.Accounts;

/// <summary>
/// Pedido de escalonamento (N4/CA-6). Descreve o PAPEL lógico, a capacidade exigida, o
/// escopo de paths do trabalho e — quando se escolhe um critic — o actor já selecionado,
/// para impor a independência actor↔critic.
/// </summary>
public sealed record AccountSchedulingRequest
{
    public required string Role { get; init; }

    /// <summary>Capacidade exigida do executor (ex.: `code`, `review`, `chat`).</summary>
    public required string RequiredCapability { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>Escopos de path que o trabalho vai reivindicar. Vazio para um critic read-only.</summary>
    public IReadOnlyList<string> RequiredPathScopes { get; init; } = [];

    /// <summary>Verdadeiro quando se seleciona o CRITIC: exige conta distinta do actor.</summary>
    public bool ForCritic { get; init; }

    /// <summary>Actor já escolhido; a mesma conta nunca pode ser actor e critic.</summary>
    public string? ActorAlias { get; init; }

    public string? RiskTier { get; init; }

    /// <summary>
    /// Snapshots de cota por alias (N4/5.1). Uma cota `Exhausted` bloqueia; `Unknown`/stale
    /// nunca bloqueia por si só — aplica-se o limite local de concorrência.
    /// </summary>
    public IReadOnlyDictionary<string, AccountQuotaSnapshot> Quotas { get; init; } =
        new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Scheduler de contas provider-agnostic (N4/CA-6). A seleção é EXPLICÁVEL por código
/// fechado — cada conta considerada carrega o motivo de ter sido elegível ou descartada — e
/// é fail-closed: preferência (prioridade) nunca sobrepõe uma recusa. Estados e razões são
/// conjuntos fechados; disponibilidade nunca é inferida da ausência de dado.
///
/// Invariantes impostas aqui:
/// - adapter ausente ⇒ Unavailable (nunca "suportado por suposição");
/// - executor instalado sem login ⇒ AuthenticationRequired;
/// - cota esgotada ⇒ QuotaLimited (bloqueia até o reset); cota desconhecida NÃO bloqueia;
/// - saúde ≠ cota e cota ≠ instalação: são checagens independentes;
/// - conta não executa acima da concorrência;
/// - mesma conta nunca é actor e critic;
/// - conflito de escopo de path impede o escalonamento.
/// </summary>
public sealed class AgentAccountScheduler
{
    private readonly Func<string, bool> _executorImplemented;

    public AgentAccountScheduler(Func<string, bool>? executorImplemented = null) =>
        _executorImplemented = executorImplemented ?? ExternalAgentExecutorFactory.IsImplemented;

    public AccountSelectionDecision Select(AgentAccountRegistry registry, AccountSchedulingRequest request)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(request);

        var candidates = registry.List()
            .Select(account => Evaluate(account, request))
            .ToArray();

        // Elegíveis por prioridade decrescente, desempate por alias determinístico. A ordem
        // é a preferência; ela NUNCA promove um inelegível.
        var eligible = candidates
            .Where(candidate => candidate.Eligible)
            .OrderByDescending(candidate => candidate.Priority)
            // A prioridade é a preferência primária; no empate vale a ordem de preferência
            // (saúde reduzida e cota perto do limite são operacionais, nunca preferidas a igual
            // prioridade — mas também nunca descartadas, porque a capacidade é real).
            .ThenBy(candidate => candidate.PreferenceRank)
            .ThenBy(candidate => candidate.Alias, StringComparer.Ordinal)
            .ToArray();

        if (eligible.Length == 0)
        {
            return new AccountSelectionDecision(
                null, "scheduler.no_eligible_account", candidates, []);
        }

        var selected = eligible[0];
        var fallbacks = eligible.Skip(1).Select(candidate => candidate.Alias).ToArray();
        return new AccountSelectionDecision(
            selected.Alias, "scheduler.selected", candidates, fallbacks);
    }

    private AccountSelectionCandidate Evaluate(
        AgentAccountContract account, AccountSchedulingRequest request)
    {
        var (eligible, reason) = Classify(account, request);
        return new AccountSelectionCandidate(
            account.Alias, eligible, reason, account.Priority, PreferenceRankOf(reason));
    }

    /// <summary>
    /// O escopo pedido está DENTRO de um escopo permitido da conta? Compara pelas raízes,
    /// tolerando o sufixo de varredura dos dois lados: `src/Modules/X/**` está dentro de `src/**`,
    /// e um path exato está dentro da raiz que o contém.
    /// </summary>
    private static bool IsWithinScope(string requested, string allowed)
    {
        var candidate = TrimScope(requested);
        var root = TrimScope(allowed);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimScope(string value)
    {
        var normalized = value.Replace('\\', '/').Trim('/');
        return normalized.EndsWith("/**", StringComparison.Ordinal)
            ? normalized[..^3].TrimEnd('/')
            : normalized;
    }

    /// <summary>
    /// Ordem de preferência entre elegíveis. Perto do limite pesa MAIS que degradada porque a
    /// conta degradada entrega devagar, enquanto a que está perto do limite tende a morrer no
    /// meio do trabalho — o custo é a tentativa inteira, não a latência.
    /// </summary>
    private static int PreferenceRankOf(string reasonCode) => reasonCode switch
    {
        "account.eligible_degraded" => 1,
        "account.eligible_near_limit" => 2,
        "account.eligible_degraded_near_limit" => 3,
        _ => 0,
    };

    private (bool Eligible, string ReasonCode) Classify(
        AgentAccountContract account, AccountSchedulingRequest request)
    {
        // 1. Papel lógico. O escopo pertence ao papel; uma conta sem o papel não concorre.
        if (!account.AllowedRoles.Contains(request.Role, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "account.role_not_allowed");
        }

        // 2. Adapter: sem implementação real, o executor é Unavailable — nunca suposto.
        if (!_executorImplemented(account.ExecutorId))
        {
            return (false, "account.adapter_not_implemented");
        }

        // 3. Capacidade declarada do executor (instalação ≠ capacidade).
        var profile = ExecutorCatalog.Find(account.ExecutorId);
        if (profile is null)
        {
            return (false, "account.executor_unknown");
        }

        if (!profile.Capabilities.Capabilities.Contains(
                request.RequiredCapability, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "account.capability_unsupported");
        }

        // 4. Estado administrativo/instalação/autenticação (fechado).
        switch (account.State)
        {
            case AgentAccountState.Disabled:
                return (false, "account.disabled");
            case AgentAccountState.Unavailable:
                return (false, "account.executor_unavailable");
            case AgentAccountState.AuthenticationRequired:
                return (false, "account.authentication_required");
            default:
                break;
        }

        // 5. Cota: esgotada bloqueia até o reset. Desconhecida/stale NÃO bloqueia (limite
        //    local conservador via concorrência). O estado QuotaLimited persistido também
        //    bloqueia enquanto o cooldown correr.
        if (request.Quotas.TryGetValue(account.Alias, out var quota) && quota.IsExhaustedAt(request.Now))
        {
            return (false, "account.quota_limited");
        }

        if (account.State == AgentAccountState.QuotaLimited &&
            (account.CooldownUntil is null || account.CooldownUntil.Value > request.Now))
        {
            return (false, "account.quota_limited");
        }

        // 6. Cooldown transitório.
        if (account.State == AgentAccountState.CoolingDown &&
            account.CooldownUntil is { } until && until > request.Now)
        {
            return (false, "account.cooling_down");
        }

        // 7. Concorrência: a conta nunca executa acima do seu limite.
        if (account.ActiveAttempts >= account.ConcurrencyLimit)
        {
            return (false, "account.concurrency_exhausted");
        }

        // 8. Escopo de path: uma conta que sequer permite o escopo nunca é escolhida (o claim real
        //    é validado depois pela política de path).
        //
        //    A comparação é por CONTINÊNCIA, não por igualdade. O escopo permitido da conta é uma
        //    RAIZ de trabalho — `frontend/**` significa "pode trabalhar dentro de frontend", não
        //    "só aceita exatamente esta string". Enquanto era igualdade, o card que pedia
        //    `frontend/src/features/approvals/**` — mais estreito e portanto mais seguro — era
        //    recusado com `path_scope_not_allowed`, e o escopo por card ficava impossível de usar
        //    justamente nas contas que declaram raízes. Observado ao vivo: quatro cards de módulos
        //    diferentes adiados a cada ciclo com a frota inteira disponível.
        foreach (var scope in request.RequiredPathScopes)
        {
            if (!account.AllowedPathScopes.Any(allowed => IsWithinScope(scope, allowed)))
            {
                return (false, "account.path_scope_not_allowed");
            }
        }

        // 9. Independência actor↔critic: a mesma conta nunca aprova o próprio trabalho.
        if (request.ForCritic && request.ActorAlias is { Length: > 0 } actor &&
            string.Equals(account.Alias, actor, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "account.actor_cannot_be_critic");
        }

        // Degradada é OPERACIONAL (saúde reduzida ≠ cota/indisponível): elegível, porém a
        // razão registra a degradação para o roteamento preferir uma saudável quando houver.
        //
        // Perto do limite é o MESMO tipo de sinal, do lado da cota. O estado existia no contrato
        // ("roteamento deve preferir alternativa") e nada o consultava: uma conta a ponto de
        // esgotar competia de igual para igual com outra cheia e, ganhando por prioridade, morria
        // no meio da tentativa. Bloqueá-la seria pior — descartaria capacidade real e pararia o
        // trabalho curto/crítico quando ela fosse a única. Então ela continua elegível e apenas
        // perde a vez enquanto houver alternativa. Medição VENCIDA não de-prefere ninguém: sem
        // proveniência fresca, não há sinal (a mesma regra que impede bloquear por dado velho).
        var nearLimit =
            request.Quotas.TryGetValue(account.Alias, out var observed) &&
            observed.Status == QuotaStatus.NearLimit &&
            !observed.IsStale(request.Now);
        var degraded = account.State == AgentAccountState.Degraded;
        return (degraded, nearLimit) switch
        {
            (true, true) => (true, "account.eligible_degraded_near_limit"),
            (true, false) => (true, "account.eligible_degraded"),
            (false, true) => (true, "account.eligible_near_limit"),
            _ => (true, "account.eligible"),
        };
    }
}
