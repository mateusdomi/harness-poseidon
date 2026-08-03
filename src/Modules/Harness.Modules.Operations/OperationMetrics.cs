using System.Globalization;

namespace Harness.Modules.Operations;

/// <summary>Uma invocação de modelo, como a medição precisa vê-la.</summary>
public readonly record struct InvocationSample(
    string AttemptId,
    string TaskId,
    /// <summary>Desfecho registrado: `completed`, `transient`, `permanent`, com prefixo `review:` quando é revisão.</summary>
    string Outcome,
    decimal CostUsd,
    long DurationMs,
    long InputTokens,
    long OutputTokens);

/// <summary>Uma tentativa de trabalho, para separar entrega de retrabalho.</summary>
public readonly record struct AttemptSample(
    string AttemptId,
    string TaskId,
    int AttemptNumber,
    /// <summary>Estado final: `approved`, `rejected`, `running`…</summary>
    string State,
    long? DurationMs);

/// <summary>Janela de uma fase, para tempo de parede.</summary>
public readonly record struct PhaseSample(int Order, TimeSpan? WallTime);

/// <summary>
/// Métricas da operação (§31/§32 da ordem executiva).
///
/// Cada número é `null` quando o denominador não existe. Isso é deliberado: um zero inventado
/// afirma "medimos e deu zero", que é diferente de "não há base para medir" — e a ordem é
/// explícita em que score vem da evidência, nunca de estimativa conveniente. Um relatório com
/// campo vazio é honesto; um com número fabricado é pior que não ter relatório.
/// </summary>
public sealed record OperationMetricsReport
{
    /// <summary>Custo total dividido pelos artefatos que foram ACEITOS (tentativa aprovada).</summary>
    public decimal? CostPerAcceptedArtifact { get; init; }

    /// <summary>Custo total dividido pelos cards que chegaram ao fim.</summary>
    public decimal? CostPerDeliveredRequirement { get; init; }

    /// <summary>Custo gasto em tentativas ALÉM da primeira do mesmo card.</summary>
    public decimal? CostOfRework { get; init; }

    /// <summary>
    /// Fração do custo queimada em falhas TRANSITÓRIAS — trabalho que não entregou nada e cuja
    /// causa não era o enunciado. A ordem cita ~18% no piloto anterior e meta &lt;5%.
    /// </summary>
    public double? TransientFailureWasteRate { get; init; }

    /// <summary>Fração dos cards aceitos já na PRIMEIRA tentativa.</summary>
    public double? FirstPassAcceptanceRate { get; init; }

    /// <summary>Duração média de uma tentativa. Sensível a outlier — leia junto com a mediana.</summary>
    public TimeSpan? MeanRunDuration { get; init; }

    /// <summary>
    /// Duração MEDIANA de uma tentativa.
    ///
    /// É a estatística honesta aqui: na medição real uma única tentativa ficou 71 horas aberta
    /// (órfã cuja duração só foi fechada na reconciliação) e sozinha puxou a média de ~4 minutos
    /// para ~84. Publicar só a média descreveria um sistema que não existe.
    /// </summary>
    public TimeSpan? MedianRunDuration { get; init; }

    /// <summary>
    /// Tempo de parede POR ORDEM DE FASE (1..N), agregado por mediana entre as execuções.
    ///
    /// Agregar é o que torna o número legível: sem isso, medir vários projetos devolvia uma
    /// lista de centenas de posições, quase toda nula, em que ninguém achava a fase.
    /// </summary>
    public IReadOnlyList<TimeSpan?> PhaseWallTime { get; init; } = [];

    /// <summary>
    /// Tempo em chamada de modelo dividido pelo tempo de parede das fases. Quanto mais baixo,
    /// mais a operação passou esperando em vez de produzindo.
    /// </summary>
    public double? ModelUtilization { get; init; }

    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    public decimal TotalCostUsd { get; init; }
    public int InvocationCount { get; init; }
}

/// <summary>
/// Calcula as métricas a partir de amostras já colhidas. Puro e sem IO: a coleta é de quem
/// chama, e assim cada número pode ser conferido em teste sem banco nem host.
/// </summary>
public static class OperationMetricsCalculator
{
    /// <summary>Desfecho de uma invocação que terminou entregando.</summary>
    private const string Completed = "completed";

    /// <summary>Desfecho transitório: morreu sem entregar e sem culpa do enunciado.</summary>
    private const string Transient = "transient";

    public static OperationMetricsReport Calculate(
        IReadOnlyList<InvocationSample> invocations,
        IReadOnlyList<AttemptSample> attempts,
        IReadOnlyList<PhaseSample> phases)
    {
        ArgumentNullException.ThrowIfNull(invocations);
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(phases);

        var totalCost = invocations.Sum(sample => sample.CostUsd);

        // Revisão não é artefato: ela julga o trabalho de outro. Contá-la como entrega inflaria
        // o denominador e faria o custo por artefato parecer menor do que é.
        var accepted = attempts.Count(sample =>
            string.Equals(sample.State, "approved", StringComparison.OrdinalIgnoreCase));

        var deliveredTasks = attempts
            .Where(sample => string.Equals(sample.State, "approved", StringComparison.OrdinalIgnoreCase))
            .Select(sample => sample.TaskId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        // Retrabalho é o que se gastou depois da primeira tentativa do mesmo card — a segunda
        // rodada existe porque a primeira não bastou.
        var reworkAttempts = attempts
            .Where(sample => sample.AttemptNumber > 1)
            .Select(sample => sample.AttemptId)
            .ToHashSet(StringComparer.Ordinal);
        var reworkCost = invocations
            .Where(sample => reworkAttempts.Contains(sample.AttemptId))
            .Sum(sample => sample.CostUsd);

        var transientCost = invocations
            .Where(sample => IsOutcome(sample.Outcome, Transient))
            .Sum(sample => sample.CostUsd);

        // Aceite de primeira: cards cuja tentativa nº 1 foi aprovada, sobre os cards que
        // chegaram a ter algum desfecho. Card ainda em voo não entra em nenhum dos lados.
        var settled = attempts
            .Where(sample => !string.Equals(sample.State, "running", StringComparison.OrdinalIgnoreCase))
            .GroupBy(sample => sample.TaskId, StringComparer.Ordinal)
            .ToArray();
        var firstPass = settled.Count(group => group.Any(sample =>
            sample.AttemptNumber == 1 &&
            string.Equals(sample.State, "approved", StringComparison.OrdinalIgnoreCase)));

        var measuredDurations = attempts
            .Where(sample => sample.DurationMs is > 0)
            .Select(sample => sample.DurationMs!.Value)
            .ToArray();

        var modelMs = invocations.Sum(sample => sample.DurationMs);

        // Uma posição por ORDEM de fase, com a mediana entre as execuções que fecharam. Fase que
        // nunca fechou permanece nula: ela não durou zero, ela ainda não terminou.
        var phaseWall = phases
            .GroupBy(sample => sample.Order)
            .OrderBy(group => group.Key)
            .Select(group => Median(
                [.. group.Where(sample => sample.WallTime is not null)
                    .Select(sample => (long)sample.WallTime!.Value.TotalMilliseconds)]))
            .Select(ms => ms is null ? (TimeSpan?)null : TimeSpan.FromMilliseconds(ms.Value))
            .ToArray();

        var wallMs = phases
            .Where(sample => sample.WallTime is not null)
            .Sum(sample => (long)sample.WallTime!.Value.TotalMilliseconds);

        return new OperationMetricsReport
        {
            InvocationCount = invocations.Count,
            TotalCostUsd = totalCost,
            TotalInputTokens = invocations.Sum(sample => sample.InputTokens),
            TotalOutputTokens = invocations.Sum(sample => sample.OutputTokens),
            CostPerAcceptedArtifact = accepted == 0 ? null : totalCost / accepted,
            CostPerDeliveredRequirement = deliveredTasks == 0 ? null : totalCost / deliveredTasks,
            CostOfRework = invocations.Count == 0 ? null : reworkCost,
            TransientFailureWasteRate = totalCost == 0 ? null : (double)(transientCost / totalCost),
            FirstPassAcceptanceRate = settled.Length == 0 ? null : (double)firstPass / settled.Length,
            MeanRunDuration = measuredDurations.Length == 0
                ? null
                : TimeSpan.FromMilliseconds(measuredDurations.Average()),
            MedianRunDuration = Median(measuredDurations) is { } median
                ? TimeSpan.FromMilliseconds(median)
                : null,
            PhaseWallTime = phaseWall,
            ModelUtilization = wallMs == 0 ? null : modelMs / (double)wallMs,
        };
    }

    /// <summary>
    /// O desfecho carrega sufixos (`permanent|usage_unknown`) e o prefixo `review:`. Comparar por
    /// igualdade exata perderia a maioria das amostras reais.
    /// </summary>
    private static bool IsOutcome(string outcome, string expected) =>
        outcome.Contains(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mediana de uma amostra. Preferida à média sempre que um único valor extremo puder
    /// descrever mal o conjunto — e aqui pode: tentativa órfã fica aberta por dias.
    /// </summary>
    private static double? Median(long[] values)
    {
        if (values.Length == 0)
        {
            return null;
        }

        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    /// <summary>Serialização estável para `METRICS.json`, com invariante de cultura.</summary>
    public static string Format(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null";
}
