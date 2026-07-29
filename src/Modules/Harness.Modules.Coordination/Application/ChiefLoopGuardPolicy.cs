namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Limites das guardas de laço. Todos configuráveis, todos com padrão conservador: a guarda existe
/// para conter um laço, não para estrangular trabalho legítimo.
/// </summary>
public sealed record ChiefLoopGuardLimits(
    int MaxSelfTriggeredTurnsPerDemand = 8,
    int MaxSelfTriggeredTurnsPerWindow = 4,
    TimeSpan? RateWindow = null)
{
    public TimeSpan Window => RateWindow ?? TimeSpan.FromMinutes(10);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSelfTriggeredTurnsPerDemand);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSelfTriggeredTurnsPerWindow);
        if (Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RateWindow), "A janela precisa ser positiva.");
        }
    }
}

/// <summary>
/// O turno que se quer abrir. <see cref="SelfTriggered"/> separa o que a Bruna disparou sozinha do
/// que veio do dono — turno pedido por humano nunca é barrado por estas guardas.
/// </summary>
public sealed record ChiefTurnTrigger(
    string DemandId,
    DateTimeOffset RequestedAt,
    bool SelfTriggered,
    string? DemandPlanId = null,
    string? CauseKey = null);

/// <summary>
/// Fatos já coletados para a decisão — puros, sem IO.
/// <see cref="SelfTriggeredTurnsForDemand"/> é o acumulado da demanda;
/// <see cref="RecentSelfTriggeredTurns"/> alimenta a taxa por janela;
/// <see cref="PlanTurnInFlight"/> é a reentrância pelo plano;
/// <see cref="CausalEdges"/> são as arestas "gerou" lidas do ledger.
/// </summary>
public sealed record ChiefLoopGuardFacts(
    int SelfTriggeredTurnsForDemand = 0,
    bool PlanTurnInFlight = false,
    IReadOnlyList<DateTimeOffset>? RecentSelfTriggeredTurns = null,
    IReadOnlyList<CausalEdge>? CausalEdges = null)
{
    public IReadOnlyList<DateTimeOffset> Recent => RecentSelfTriggeredTurns ?? [];

    public IReadOnlyList<CausalEdge> Edges => CausalEdges ?? [];
}

/// <summary>
/// Veredito da guarda. Quando <see cref="Allowed"/> é falso, <see cref="ReasonCode"/> e
/// <see cref="Cycle"/> são a evidência do evento auditado — a interrupção é registrada, não silenciosa.
/// </summary>
public sealed record ChiefLoopGuardVerdict(
    bool Allowed,
    string ReasonCode,
    string? Detail = null,
    CausalCycle? Cycle = null);

/// <summary>
/// Guardas de laço da Bruna (B9).
///
/// Um agente que planeja e também executa o próprio plano pode entrar em laço sem nunca cometer um
/// erro visível: cada turno, isolado, é razoável. O laço só existe na sequência. Estas quatro
/// guardas cobrem as quatro formas que ele assume, do sintoma mais grosseiro à causa mais funda:
///
/// 1. <b>Teto por demanda</b> — quantos turnos a Bruna já disparou sozinha para a MESMA demanda.
///    Grosseiro de propósito: é a rede que segura o caso não previsto pelas outras três.
/// 2. <b>Reentrância por plano</b> — um plano com turno em voo não abre outro. Sem isso, dois
///    turnos do mesmo plano se reforçam mutuamente e a contagem por demanda cresce em dobro.
/// 3. <b>Taxa por janela</b> — laço rápido é diferente de trabalho longo. A janela distingue os
///    dois sem punir a demanda que legitimamente exige muitos turnos ao longo do dia.
/// 4. <b>Ciclo causal</b> — a única que vê o laço pelo que ele é: A gerou B que regenerou A. As três
///    anteriores adiam; esta interrompe e explica.
///
/// Turno disparado pelo dono jamais é barrado: a guarda existe contra a autoalimentação da Bruna,
/// não contra o usuário.
/// </summary>
public static class ChiefLoopGuardPolicy
{
    public const string ReasonAllowed = "chief_loop.allowed";
    public const string ReasonHumanTriggered = "chief_loop.human_triggered";
    public const string ReasonDemandCeiling = "chief_loop.demand_ceiling";
    public const string ReasonPlanReentrancy = "chief_loop.plan_reentrancy";
    public const string ReasonRateWindow = "chief_loop.rate_window";
    public const string ReasonCausalCycle = "chief_loop.causal_cycle";

    public static ChiefLoopGuardVerdict Evaluate(
        ChiefTurnTrigger trigger,
        ChiefLoopGuardFacts facts,
        ChiefLoopGuardLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger.DemandId);

        var effectiveLimits = limits ?? new ChiefLoopGuardLimits();
        effectiveLimits.Validate();

        // O dono pedindo é o dono pedindo. Nenhuma guarda se aplica.
        if (!trigger.SelfTriggered)
        {
            return new ChiefLoopGuardVerdict(Allowed: true, ReasonHumanTriggered);
        }

        // Ciclo causal primeiro: ele é a causa, e as outras guardas são sintomas dele.
        if (trigger.CauseKey is { } causeKey && !string.IsNullOrWhiteSpace(causeKey))
        {
            var effectKey = DemandKey(trigger.DemandId);
            var cycle = CausalCycleDetector.DetectClosingCycle(facts.Edges, causeKey, effectKey);
            if (cycle is not null)
            {
                return new ChiefLoopGuardVerdict(
                    Allowed: false,
                    ReasonCausalCycle,
                    cycle.Describe(),
                    cycle);
            }
        }

        if (facts.PlanTurnInFlight && !string.IsNullOrWhiteSpace(trigger.DemandPlanId))
        {
            return new ChiefLoopGuardVerdict(
                Allowed: false,
                ReasonPlanReentrancy,
                $"O plano {trigger.DemandPlanId} já tem um turno em voo.");
        }

        if (facts.SelfTriggeredTurnsForDemand >= effectiveLimits.MaxSelfTriggeredTurnsPerDemand)
        {
            return new ChiefLoopGuardVerdict(
                Allowed: false,
                ReasonDemandCeiling,
                $"{facts.SelfTriggeredTurnsForDemand} turnos autodisparados nesta demanda " +
                $"(teto {effectiveLimits.MaxSelfTriggeredTurnsPerDemand}).");
        }

        var windowStart = trigger.RequestedAt - effectiveLimits.Window;
        var inWindow = facts.Recent.Count(timestamp =>
            timestamp > windowStart && timestamp <= trigger.RequestedAt);
        if (inWindow >= effectiveLimits.MaxSelfTriggeredTurnsPerWindow)
        {
            return new ChiefLoopGuardVerdict(
                Allowed: false,
                ReasonRateWindow,
                $"{inWindow} turnos autodisparados em {effectiveLimits.Window.TotalMinutes:0} minutos " +
                $"(teto {effectiveLimits.MaxSelfTriggeredTurnsPerWindow}).");
        }

        return new ChiefLoopGuardVerdict(Allowed: true, ReasonAllowed);
    }

    /// <summary>Chave causal canônica de uma demanda no ledger.</summary>
    public static string DemandKey(string demandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        return $"demand:{demandId.Trim()}";
    }

    /// <summary>Chave causal canônica de um plano no ledger.</summary>
    public static string PlanKey(string demandPlanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandPlanId);
        return $"plan:{demandPlanId.Trim()}";
    }

    /// <summary>Chave causal canônica de um card no ledger.</summary>
    public static string CardKey(string cardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        return $"card:{cardId.Trim()}";
    }
}
