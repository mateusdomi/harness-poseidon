namespace Harness.Modules.Coordination.Application;

/// <summary>O par que se mede: quem executou, com que capacidade de modelo, em que tipo de card.</summary>
public sealed record CapabilityPair(string AccountAlias, string ModelTier, string CardType);

/// <summary>Uma tentativa encerrada, do ponto de vista da medição.</summary>
public sealed record AttemptOutcome(
    CapabilityPair Pair,
    string TaskId,
    int AttemptNumber,
    bool Succeeded);

/// <summary>
/// pass@k de um par. <see cref="PassAt1"/> é a taxa de acerto na primeira rodada;
/// <see cref="PassAtK"/> é a taxa de cards resolvidos em até k rodadas.
/// </summary>
public sealed record PassAtKMeasurement(
    CapabilityPair Pair,
    int K,
    int TasksObserved,
    int TasksSolvedWithinK,
    int TasksSolvedFirstTry)
{
    public double PassAtK => TasksObserved == 0 ? 0d : (double)TasksSolvedWithinK / TasksObserved;

    public double PassAt1 => TasksObserved == 0 ? 0d : (double)TasksSolvedFirstTry / TasksObserved;

    /// <summary>
    /// Quanto o par ganha com uma segunda chance. Um par com pass@1 baixo e pass@k alto é útil e
    /// barato; um com os dois iguais não melhora com retentativa — nele, retentar é gastar cota
    /// para repetir o mesmo resultado.
    /// </summary>
    public double RetryGain => PassAtK - PassAt1;
}

/// <summary>
/// pass@k por par agente+modelo+tipo de card (B12).
///
/// A métrica que existia era binário por tentativa, e ela esconde a informação que decide roteamento:
/// um par pode acertar 40% na primeira rodada e 90% em três — nele, retentar é barato e eficaz.
/// Outro pode acertar 55% na primeira e 57% em três: retentar ali é queimar cota para repetir o
/// mesmo resultado, e o certo é trocar de par ou replanejar o card.
///
/// A unidade de contagem é o CARD, não a tentativa. Contar tentativas premiaria quem tenta muito:
/// um par que gasta cinco rodadas em cada card teria mais "sucessos" absolutos que outro que resolve
/// de primeira. O que interessa é quantos cards ficaram resolvidos, e em quantas rodadas.
/// </summary>
public static class PassAtKPolicy
{
    /// <summary>
    /// Mede o par a partir do histórico. Tentativas de outros pares são ignoradas em silêncio: a
    /// medição é por par, e misturá-las produziria uma média que não descreve ninguém.
    /// </summary>
    public static PassAtKMeasurement Measure(
        CapabilityPair pair,
        IReadOnlyList<AttemptOutcome> history,
        int k)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        var relevant = history
            .Where(outcome => Equals(outcome.Pair, pair))
            .GroupBy(outcome => outcome.TaskId, StringComparer.Ordinal)
            .ToArray();

        var solvedWithinK = 0;
        var solvedFirstTry = 0;
        foreach (var task in relevant)
        {
            var attempts = task.OrderBy(outcome => outcome.AttemptNumber).ToArray();
            if (attempts.Any(outcome => outcome.Succeeded && outcome.AttemptNumber <= k))
            {
                solvedWithinK++;
            }

            if (attempts.FirstOrDefault(outcome => outcome.AttemptNumber == 1)?.Succeeded == true)
            {
                solvedFirstTry++;
            }
        }

        return new PassAtKMeasurement(pair, k, relevant.Length, solvedWithinK, solvedFirstTry);
    }

    /// <summary>
    /// Quantas rodadas vale conceder a este par. Um par que não melhora com retentativa recebe uma
    /// rodada; conceder mais é gastar cota sabendo que o resultado se repete.
    /// </summary>
    public static int RecommendMaxRounds(
        PassAtKMeasurement measurement,
        int ceiling,
        double meaningfulGain = 0.10d)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);

        // Sem histórico suficiente, não se inventa recomendação: vale o teto da política de esforço.
        if (measurement.TasksObserved < 3)
        {
            return ceiling;
        }

        return measurement.RetryGain >= meaningfulGain ? ceiling : 1;
    }

    /// <summary>
    /// Ordena candidatos para o mesmo tipo de card: quem resolve mais dentro de k primeiro, com
    /// desempate por acerto de primeira (menos rodadas para o mesmo resultado é melhor).
    /// </summary>
    public static IReadOnlyList<PassAtKMeasurement> Rank(
        IReadOnlyList<PassAtKMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        return measurements
            .OrderByDescending(measurement => measurement.PassAtK)
            .ThenByDescending(measurement => measurement.PassAt1)
            .ThenBy(measurement => measurement.Pair.AccountAlias, StringComparer.Ordinal)
            .ToArray();
    }
}
