namespace Harness.Modules.Delivery.Application;

/// <summary>
/// DEL-09 — Previsão honesta. Componente PURO e determinístico que NUNCA inventa uma data.
///
/// Dada a situação do plano (marcos concluídos, dependências abertas, validações pendentes, variação
/// média histórica e a data comprometida), devolve { data?, confiança, base[] }. Regras de honestidade:
///  1. Sem marcos no plano → SEM data (evidência insuficiente): não há de onde projetar.
///  2. Sem data comprometida → SEM data: não há âncora de calendário; não fabricamos uma.
///  3. Com marcos + data comprometida → a data comprometida é a âncora; a variação MÉDIA HISTÓRICA
///     REGISTRADA (nunca um buffer arbitrário) a ajusta. Sem histórico de variação, a data é a própria
///     comprometida, com confiança reduzida.
/// Cada saída acompanha uma BASE que enumera os sinais usados, tornando a previsão auditável.
/// O <see cref="ForecastResult.ConfidencePercent"/> é um SCORE grosseiro derivado — não uma probabilidade.
/// </summary>
public static class DeliveryForecaster
{
    public static ForecastResult Forecast(ForecastInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.MilestonesTotal < 0 || input.MilestonesDone < 0 ||
            input.OpenDependencies < 0 || input.PendingValidations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Counts must be non-negative.");
        }

        if (input.MilestonesDone > input.MilestonesTotal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), "Completed milestones cannot exceed the total.");
        }

        var hasMilestones = input.MilestonesTotal > 0;
        var hasCommitted = input.CommittedDate.HasValue;
        var hasVariance = input.AverageVarianceDays.HasValue;
        var completionRatio = hasMilestones
            ? input.MilestonesDone / (double)input.MilestonesTotal
            : 0d;
        var blockers = input.OpenDependencies + input.PendingValidations;

        var basis = new List<ForecastBasis>
        {
            new("milestone_progress",
                $"{input.MilestonesDone}/{input.MilestonesTotal} milestones complete"),
            new("open_dependencies", $"{input.OpenDependencies} open dependencies"),
            new("pending_validations", $"{input.PendingValidations} pending validations"),
            hasVariance
                ? new ForecastBasis("variance_history",
                    $"average recorded variance {Signed(input.AverageVarianceDays!.Value)} day(s)")
                : new ForecastBasis("variance_history",
                    "no completed-milestone variance history yet"),
            hasCommitted
                ? new ForecastBasis("committed_date",
                    $"committed date {Iso(input.CommittedDate!.Value)}")
                : new ForecastBasis("committed_date", "no committed date recorded"),
        };

        var score = ComputeScore(hasVariance, completionRatio, blockers);

        // Regra 1: sem marcos → nada de onde projetar. Data nula, confiança baixa.
        if (!hasMilestones)
        {
            basis.Add(new ForecastBasis(
                "insufficient_evidence", "no plan milestones to project a delivery date from"));
            return Insufficient(score, basis);
        }

        // Regra 2: sem data comprometida → sem âncora de calendário. Não inventamos uma data.
        if (!hasCommitted)
        {
            basis.Add(new ForecastBasis(
                "insufficient_evidence", "no committed date to anchor a forecast on"));
            return Insufficient(score, basis);
        }

        // Regra 3: projeta a partir da data comprometida ajustada pela variação MÉDIA HISTÓRICA
        // registrada (arredondada para dias inteiros). Sem histórico, o ajuste é zero (usa a própria
        // data comprometida) — nunca um buffer arbitrário.
        var varianceDays = hasVariance
            ? (int)Math.Round(input.AverageVarianceDays!.Value, MidpointRounding.AwayFromZero)
            : 0;
        var forecastDate = input.CommittedDate!.Value.AddDays(varianceDays);

        var confidence = score >= 70
            ? ForecastConfidence.High
            : score >= 45
                ? ForecastConfidence.Medium
                : ForecastConfidence.Low;

        return new ForecastResult(forecastDate, confidence, score, true, basis);
    }

    private static ForecastResult Insufficient(int score, List<ForecastBasis> basis) =>
        new(null, ForecastConfidence.Low, Math.Min(score, 20), false, basis);

    // Score 0..100 transparente e determinístico: base 40, +20 com histórico de variação, até +30
    // por proporção de marcos concluídos, -12 por bloqueador (dependência/validação) até 4. Grosseiro
    // por design — comunica confiança relativa, não uma probabilidade calibrada.
    private static int ComputeScore(bool hasVariance, double completionRatio, int blockers)
    {
        var score = 40;
        if (hasVariance)
        {
            score += 20;
        }

        score += (int)Math.Round(completionRatio * 30, MidpointRounding.AwayFromZero);
        score -= Math.Min(blockers, 4) * 12;
        return Math.Clamp(score, 5, 95);
    }

    private static string Signed(double value) =>
        value.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string ToConfidenceString(ForecastConfidence confidence) => confidence switch
    {
        ForecastConfidence.High => "high",
        ForecastConfidence.Medium => "medium",
        _ => "low",
    };
}

public sealed record ForecastInput(
    int MilestonesTotal,
    int MilestonesDone,
    int OpenDependencies,
    int PendingValidations,
    double? AverageVarianceDays,
    DateTimeOffset? CommittedDate,
    DateTimeOffset AsOf);

public enum ForecastConfidence
{
    Low,
    Medium,
    High,
}

public sealed record ForecastBasis(string Signal, string Detail);

public sealed record ForecastResult(
    DateTimeOffset? ForecastDate,
    ForecastConfidence Confidence,
    int ConfidencePercent,
    bool HasSufficientEvidence,
    IReadOnlyList<ForecastBasis> Basis);
