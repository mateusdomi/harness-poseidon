namespace Harness.Modules.Governance.Metrics;

/// <summary>
/// PLAT-04: detector PURO e determinístico de tarefa "travada" (sem progresso semântico).
///
/// Entrada: o histórico ORDENADO de tentativas de UMA tarefa. Saída: um veredito tipado com a
/// razão. Não muta nada e não conhece I/O — apenas raciocina sobre o histórico. Sinaliza travamento
/// quando há N falhas terminais consecutivas sem sucesso intermediário, ou quando as tentativas
/// recentes repetem a mesma instrução (hash) ou a mesma razão de falha (loop semântico).
/// </summary>
public sealed class SemanticStuckDetector(StuckDetectorOptions? options = null)
{
    private readonly StuckDetectorOptions _options = options ?? new StuckDetectorOptions();

    public StuckVerdict Evaluate(IReadOnlyList<StuckAttemptSignal> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            return StuckVerdict.NotStuck(0);
        }

        // Sequência trailing de FALHAS terminais consecutivas (para no primeiro sucesso — progresso
        // — ou no primeiro item pendente ao final, que ainda não é um desfecho).
        var trailingFailures = 0;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index].Outcome == StuckOutcome.Failed)
            {
                trailingFailures++;
            }
            else
            {
                break;
            }
        }

        var repeat = Math.Max(2, _options.RepeatThreshold);
        var noProgress = Math.Max(1, _options.NoProgressThreshold);

        // Loop de razão de falha idêntica: as últimas `repeat` falhas trailing compartilham a mesma
        // razão não vazia. É a evidência mais específica de que a tarefa está presa no mesmo erro.
        if (trailingFailures >= repeat &&
            SharedTrailingValue(history, repeat, signal => signal.FailureReason) is { } sharedReason)
        {
            return new StuckVerdict(
                true, StuckReason.RepeatedFailureReason, trailingFailures,
                $"Last {repeat} attempts failed with the same reason: {sharedReason}");
        }

        // Loop de instrução: as últimas `repeat` tentativas reexecutam a mesma instrução (hash) sem
        // nenhum sucesso entre elas — o Chief está reemitindo a mesma coisa.
        if (TrailingWithoutSuccess(history, repeat) &&
            SharedTrailingValue(history, repeat, signal => signal.InstructionHash) is { } sharedHash)
        {
            return new StuckVerdict(
                true, StuckReason.RepeatedInstruction, trailingFailures,
                $"Last {repeat} attempts reran the same instruction ({Short(sharedHash)}) without progress");
        }

        // Falhas consecutivas acima do limiar, sem um padrão mais específico.
        if (trailingFailures >= noProgress)
        {
            return new StuckVerdict(
                true, StuckReason.ConsecutiveFailures, trailingFailures,
                $"{trailingFailures} consecutive attempts failed without progress");
        }

        return StuckVerdict.NotStuck(trailingFailures);
    }

    private static bool TrailingWithoutSuccess(IReadOnlyList<StuckAttemptSignal> history, int count)
    {
        if (history.Count < count)
        {
            return false;
        }

        for (var index = history.Count - count; index < history.Count; index++)
        {
            if (history[index].Outcome == StuckOutcome.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    // Retorna o valor compartilhado pelas últimas `count` tentativas para o seletor dado, ou null
    // quando algum é vazio ou divergem.
    private static string? SharedTrailingValue(
        IReadOnlyList<StuckAttemptSignal> history, int count,
        Func<StuckAttemptSignal, string?> selector)
    {
        if (history.Count < count)
        {
            return null;
        }

        var candidate = selector(history[^1]);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        for (var index = history.Count - count; index < history.Count; index++)
        {
            if (!string.Equals(selector(history[index]), candidate, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return candidate;
    }

    private static string Short(string value) => value.Length <= 12 ? value : value[..12];
}

public sealed record StuckDetectorOptions
{
    /// <summary>Falhas terminais consecutivas que caracterizam ausência de progresso.</summary>
    public int NoProgressThreshold { get; init; } = 3;

    /// <summary>Repetições idênticas (instrução/razão) que caracterizam loop semântico.</summary>
    public int RepeatThreshold { get; init; } = 2;
}

public enum StuckOutcome
{
    Pending,
    Succeeded,
    Failed,
}

public enum StuckReason
{
    None,
    ConsecutiveFailures,
    RepeatedInstruction,
    RepeatedFailureReason,
}

public sealed record StuckAttemptSignal(
    int AttemptNumber, StuckOutcome Outcome, string? FailureReason, string? InstructionHash);

public sealed record StuckVerdict(
    bool IsStuck, StuckReason Reason, int NoProgressStreak, string Detail)
{
    public static StuckVerdict NotStuck(int streak) =>
        new(false, StuckReason.None, streak, "No stuck signal detected.");
}
