namespace Harness.Modules.Operations;

/// <summary>O que fazer depois que uma Integradora saiu.</summary>
public enum RelaunchDecision
{
    /// <summary>Relançar depois do atraso indicado.</summary>
    Relaunch,

    /// <summary>Cota/capacidade do provedor: esperar o reset, não insistir.</summary>
    WaitExternal,

    /// <summary>O comando não roda (binário ausente, configuração faltando). Insistir é laço.</summary>
    Abort,
}

/// <summary>Resultado da política, já com o atraso e o motivo legível.</summary>
public sealed record RelaunchVerdict(RelaunchDecision Decision, TimeSpan Delay, string Reason);

/// <summary>Como uma sessão de Integradora terminou, em fatos.</summary>
public sealed record IntegratorOutcome(
    int ExitCode,
    TimeSpan Duration,
    string OutputTail,
    int ConsecutiveShortRuns);

/// <summary>
/// Decide se, quando e com qual intervalo relançar a Integradora.
///
/// Existe porque as duas falhas possíveis são simétricas e ambas custam a noite: parar cedo
/// deixa trabalho conhecido em aberto, e relançar cegamente vira fork bomb — uma sessão que
/// morre em dois segundos por cota, relançada cem vezes, queima o resto do orçamento sem
/// produzir uma linha. A defesa é olhar a DURAÇÃO, não só o código de saída: uma sessão que
/// trabalhou meia hora e saiu é um yield saudável; uma que saiu em segundos, repetidamente,
/// está falhando na largada.
///
/// A classificação de cota é por texto porque o CLI externo não expõe código de saída
/// próprio para isso. É um caso legítimo de heurística de substring: aqui o pior erro é
/// esperar um reset que não existia (custo: um ciclo), enquanto no julgamento de cards o
/// erro cobrava replanejamento indevido. Na dúvida, o padrão continua sendo relançar.
/// </summary>
public static class IntegratorRelaunchPolicy
{
    /// <summary>Abaixo disso a sessão não chegou a trabalhar: é falha de largada.</summary>
    public static readonly TimeSpan ShortRunThreshold = TimeSpan.FromSeconds(90);

    /// <summary>Teto do backoff: mesmo em falha repetida, o supervisor volta a tentar.</summary>
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(30);

    /// <summary>Espera padrão de cota quando o provedor não diz quando reseta.</summary>
    public static readonly TimeSpan DefaultQuotaCooldown = TimeSpan.FromMinutes(45);

    private static readonly string[] QuotaMarkers =
    [
        "usage limit",
        "rate limit",
        "quota",
        "too many requests",
        "429",
        "limite de uso",
        "credit balance",
        "insufficient_quota",
        "overloaded_error",
    ];

    private static readonly string[] MissingCommandMarkers =
    [
        "command not found",
        "no such file or directory",
        "not recognized as an internal",
    ];

    public static RelaunchVerdict Decide(IntegratorOutcome outcome, TimeSpan baseCooldown)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var tail = outcome.OutputTail ?? string.Empty;

        // Comando inexistente nunca melhora com repetição: só o operador conserta.
        if (outcome.Duration < ShortRunThreshold && Mentions(tail, MissingCommandMarkers))
        {
            return new RelaunchVerdict(
                RelaunchDecision.Abort,
                TimeSpan.Zero,
                "comando da Integradora não existe neste host — corrigir POSEIDON_INTEGRATOR_COMMAND");
        }

        if (outcome.ExitCode == -1)
        {
            return new RelaunchVerdict(
                RelaunchDecision.Abort,
                TimeSpan.Zero,
                "POSEIDON_INTEGRATOR_COMMAND não definido");
        }

        if (Mentions(tail, QuotaMarkers))
        {
            return new RelaunchVerdict(
                RelaunchDecision.WaitExternal,
                DefaultQuotaCooldown,
                "cota/capacidade do provedor: aguardando reset em vez de insistir");
        }

        // Yield saudável: a sessão trabalhou. Relança já, sem penalidade.
        if (outcome.Duration >= ShortRunThreshold)
        {
            return new RelaunchVerdict(RelaunchDecision.Relaunch, baseCooldown, "yield após trabalho");
        }

        var backoff = Backoff(baseCooldown, outcome.ConsecutiveShortRuns);
        return new RelaunchVerdict(
            RelaunchDecision.Relaunch,
            backoff,
            $"sessão durou {outcome.Duration.TotalSeconds:F0}s ({outcome.ConsecutiveShortRuns} curta(s) seguida(s)) — recuando");
    }

    /// <summary>Backoff exponencial com teto. O teto importa: sem ele, uma falha transitória
    /// no meio da noite empurraria a próxima tentativa para depois do amanhecer.</summary>
    public static TimeSpan Backoff(TimeSpan baseCooldown, int consecutiveShortRuns)
    {
        var exponent = Math.Min(Math.Max(consecutiveShortRuns, 1), 8);
        var seconds = baseCooldown.TotalSeconds * Math.Pow(2, exponent - 1);
        return seconds >= MaximumBackoff.TotalSeconds ? MaximumBackoff : TimeSpan.FromSeconds(seconds);
    }

    private static bool Mentions(string text, string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
