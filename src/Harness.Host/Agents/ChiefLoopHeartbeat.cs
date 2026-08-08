namespace Harness.Host.Agents;

/// <summary>
/// Estado compartilhado de VIVACIDADE do loop do Chefe. O loop ESCREVE (a cada ciclo); o watchdog
/// e o endpoint <c>/health</c> LEEM. Determinístico por construção: só aritmética de timestamps,
/// nenhuma chamada de modelo, nenhuma aleatoriedade — a mesma entrada sempre produz o mesmo veredito.
///
/// Existe por dois defeitos reais observados em 2026-08-08:
///
/// 1. FALSO VERDE. O processo respondia <c>/health: healthy</c> com o loop de fato TRAVADO ou
///    ocioso — porque o health só checava o servidor web, nunca o laço que produz trabalho. Um
///    supervisor externo não tinha como saber que a fábrica tinha parado.
///
/// 2. OCIOSIDADE CEGA. A fábrica ficou ~2h sem despachar enquanto havia executor com cota e o dono
///    achava que havia trabalho travado. Ninguém provava, a cada instante, a invariante do dono:
///    "se existe card DESPACHÁVEL e existe EXECUTOR ELEGÍVEL, a fábrica não pode ficar parada".
///
/// Este objeto é a fonte única contra a qual essa invariante é verificada. Ele não decide nem age —
/// só registra o pulso e responde, sob demanda, um <see cref="ChiefLoopLiveness"/> honesto.
/// </summary>
public sealed class ChiefLoopHeartbeat
{
    private readonly object _gate = new();
    private readonly DateTimeOffset _processStartedAt;
    private DateTimeOffset? _cycleStartedAt;
    private DateTimeOffset? _cycleCompletedAt;
    private DateTimeOffset? _lastDispatchAt;
    private long _cyclesCompleted;
    private int _dispatchableCards;
    private int _eligibleAgents;

    public ChiefLoopHeartbeat(DateTimeOffset processStartedAt) => _processStartedAt = processStartedAt;

    /// <summary>Marca a ABERTURA de um ciclo. Se o ciclo travar, a distância até aqui é a prova.</summary>
    public void CycleStarted(DateTimeOffset now)
    {
        lock (_gate)
        {
            _cycleStartedAt = now;
        }
    }

    /// <summary>
    /// Marca o FECHAMENTO de um ciclo com a fotografia honesta do que ele viu: quantos cards eram
    /// realmente despacháveis, quantos executores elegíveis existiam e quantos foram despachados
    /// (para mover a régua da ociosidade só quando trabalho REAL saiu).
    /// </summary>
    public void CycleCompleted(DateTimeOffset now, int dispatchableCards, int eligibleAgents, int dispatched)
    {
        lock (_gate)
        {
            _cycleCompletedAt = now;
            _cyclesCompleted++;
            _dispatchableCards = dispatchableCards;
            _eligibleAgents = eligibleAgents;
            if (dispatched > 0)
            {
                _lastDispatchAt = now;
            }
        }
    }

    /// <summary>
    /// Veredito determinístico sobre o pulso do loop, dados os limiares do watchdog.
    /// </summary>
    /// <param name="stuckAfter">Sem ciclo concluído (ou ciclo aberto sem fechar) por mais que isto = TRAVADO.</param>
    /// <param name="idleBudget">Sem despacho por mais que isto, HAVENDO trabalho e executor = VIOLAÇÃO.</param>
    /// <param name="startupGrace">Silêncio esperado logo após a subida do processo (deploy cancela o que voava).</param>
    public ChiefLoopLiveness Read(
        DateTimeOffset now, TimeSpan stuckAfter, TimeSpan idleBudget, TimeSpan startupGrace)
    {
        lock (_gate)
        {
            var sinceStart = now - _processStartedAt;
            var withinGrace = sinceStart < startupGrace;

            var cycleOpen = _cycleStartedAt is { } started &&
                (_cycleCompletedAt is not { } completed || started > completed);

            // TRAVADO tem duas formas, ambas capturadas pela mesma pergunta — "há quanto tempo o
            // loop não dá sinal de progresso?":
            //   (a) um ciclo abriu e não fechou (hang dentro do tick — uma chamada de executor que
            //       nunca retorna, um deadlock de I/O);
            //   (b) nenhum ciclo abre há tempo demais (o próprio ExecuteAsync morreu ou o Task.Delay
            //       nunca voltou).
            // Fora da carência, qualquer uma além de `stuckAfter` é travamento.
            var lastPulse = Max(_cycleStartedAt, _cycleCompletedAt) ?? _processStartedAt;
            var stalenessSeconds = (now - lastPulse).TotalSeconds;
            var stuck = !withinGrace && stalenessSeconds >= stuckAfter.TotalSeconds;

            // VIOLAÇÃO DA INVARIANTE DO DONO: existe card despachável E executor elegível, e mesmo
            // assim nada foi despachado dentro do orçamento de ociosidade. Conservador de propósito
            // (só acusa com AMBOS > 0) para nunca gritar à toa — alarme falso ensina a ignorar o
            // canal, que é o único jeito de o vigia falhar quando estiver certo.
            var lastDispatch = _lastDispatchAt ?? _processStartedAt;
            var idleSeconds = (now - lastDispatch).TotalSeconds;
            var antiIdleViolation = !withinGrace
                && !stuck
                && _dispatchableCards > 0
                && _eligibleAgents > 0
                && idleSeconds >= idleBudget.TotalSeconds;

            return new ChiefLoopLiveness(
                Stuck: stuck,
                AntiIdleViolation: antiIdleViolation,
                StalenessSeconds: (int)Math.Round(stalenessSeconds),
                IdleSeconds: (int)Math.Round(idleSeconds),
                DispatchableCards: _dispatchableCards,
                EligibleAgents: _eligibleAgents,
                CyclesCompleted: _cyclesCompleted,
                WithinStartupGrace: withinGrace);
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        (a, b) switch
        {
            (null, _) => b,
            (_, null) => a,
            _ => a >= b ? a : b,
        };
}

/// <summary>Fotografia imutável do pulso do loop, servida ao watchdog e ao <c>/health</c>.</summary>
public sealed record ChiefLoopLiveness(
    bool Stuck,
    bool AntiIdleViolation,
    int StalenessSeconds,
    int IdleSeconds,
    int DispatchableCards,
    int EligibleAgents,
    long CyclesCompleted,
    bool WithinStartupGrace)
{
    /// <summary>Saudável = nem travado nem violando a invariante de ociosidade.</summary>
    public bool Healthy => !Stuck && !AntiIdleViolation;
}
