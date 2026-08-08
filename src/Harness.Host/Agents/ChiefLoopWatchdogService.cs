using Harness.SharedKernel.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Agents;

/// <summary>
/// VIGIA DETERMINÍSTICO da vivacidade do loop do Chefe. Roda em cadência fixa e faz uma pergunta
/// que o resto do sistema não fazia: "a fábrica está de fato PRODUZINDO, ou só parece viva?".
///
/// Não despacha, não julga card, não gasta cota de modelo — só lê o <see cref="ChiefLoopHeartbeat"/>
/// e reage a dois veredictos:
///
///   • TRAVADO — nenhum ciclo dá sinal de progresso há tempo demais (hang dentro do tick ou o
///     próprio laço morto). O <c>/health</c> passa a 503 (o falso verde acaba) e o supervisor
///     externo, que só depende de <c>curl</c>, reinicia o host. Recuperação garantida por não
///     depender do processo travado para se salvar.
///
///   • OCIOSIDADE INDEVIDA — existe card despachável E executor elegível, e mesmo assim nada foi
///     despachado dentro do orçamento do dono. Isso é BUG de despacho, não fim de fila: o vigia
///     grita alto no log (nível Crítico) para a próxima sessão de supervisão agir sobre a causa,
///     em vez de a fábrica adormecer em silêncio.
///
/// É de propósito o componente mais burro da casa: só timestamps. Um vigia esperto é um vigia que
/// pode errar junto com o que ele vigia.
/// </summary>
public sealed partial class ChiefLoopWatchdogService(
    ChiefLoopHeartbeat heartbeat,
    AgentRunSettings settings,
    IClock clock,
    ILogger<ChiefLoopWatchdogService> logger) : BackgroundService
{
    private bool _stuckAnnounced;
    private bool _idleAnnounced;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.LoopWatchdogInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Evaluate();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogWatchdogFault(logger, exception.GetType().Name);
            }
        }
    }

    private void Evaluate()
    {
        var liveness = heartbeat.Read(
            clock.UtcNow,
            settings.LoopStuckAfter,
            settings.LoopIdleBudget,
            settings.LoopLivenessStartupGrace);

        // TRAVADO — anuncia na transição (são→travado) e de novo quando volta, nunca a cada tick.
        if (liveness.Stuck && !_stuckAnnounced)
        {
            _stuckAnnounced = true;
            LogLoopStuck(logger, liveness.StalenessSeconds, liveness.CyclesCompleted);
        }
        else if (!liveness.Stuck && _stuckAnnounced)
        {
            _stuckAnnounced = false;
            LogLoopRecovered(logger, liveness.CyclesCompleted);
        }

        // OCIOSIDADE INDEVIDA — mesma disciplina de transição.
        if (liveness.AntiIdleViolation && !_idleAnnounced)
        {
            _idleAnnounced = true;
            LogAntiIdleViolation(
                logger, liveness.IdleSeconds, liveness.DispatchableCards, liveness.EligibleAgents);
        }
        else if (!liveness.AntiIdleViolation && _idleAnnounced)
        {
            _idleAnnounced = false;
            LogAntiIdleCleared(logger, liveness.DispatchableCards, liveness.EligibleAgents);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Watchdog: loop do Chefe TRAVADO — {StalenessSeconds}s sem sinal de progresso ({CyclesCompleted} ciclos concluídos até aqui). /health vira 503; supervisor externo deve reiniciar o host.")]
    private static partial void LogLoopStuck(ILogger logger, int stalenessSeconds, long cyclesCompleted);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Watchdog: loop do Chefe VOLTOU a dar sinal de progresso ({CyclesCompleted} ciclos concluídos).")]
    private static partial void LogLoopRecovered(ILogger logger, long cyclesCompleted);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Watchdog: OCIOSIDADE INDEVIDA — {IdleSeconds}s sem despacho havendo {DispatchableCards} card(s) despachável(is) e {EligibleAgents} executor(es) elegível(is). Isto é bug de despacho, não fim de fila.")]
    private static partial void LogAntiIdleViolation(
        ILogger logger, int idleSeconds, int dispatchableCards, int eligibleAgents);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Watchdog: ociosidade indevida RESOLVIDA (despacháveis={DispatchableCards}, elegíveis={EligibleAgents}).")]
    private static partial void LogAntiIdleCleared(
        ILogger logger, int dispatchableCards, int eligibleAgents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watchdog: falha ao avaliar vivacidade ({Fault}); tento de novo no próximo tick.")]
    private static partial void LogWatchdogFault(ILogger logger, string fault);
}
