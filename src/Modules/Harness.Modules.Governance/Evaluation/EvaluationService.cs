using System.Globalization;

namespace Harness.Modules.Governance.Evaluation;

/// <summary>
/// Desempenho agregado por agente, modelo, provedor e assinatura de tarefa (Fase 5 / N5).
/// </summary>
public sealed record PerformanceAggregate(
    string TargetId,
    string? Model,
    string? Provider,
    string? TaskSignature,
    int SampleSize,
    int SuccessCount,
    int FailureCount,
    double PassRate,
    double CompositeScore,
    double ConfidenceIntervalLower,
    double ConfidenceIntervalUpper,
    bool SampleSizeQualified,
    string? AccountAlias = null);

/// <summary>
/// Recomendação estruturada gerada pelo Evaluation Service com evidência estatística.
/// </summary>
public sealed record EvaluationRecommendation(
    string TargetId,
    string? Model,
    string? Provider,
    string? TaskSignature,
    string Action,
    double Score,
    double ConfidenceIntervalLower,
    double ConfidenceIntervalUpper,
    int SampleSize,
    string RecommendationReason,
    DateTimeOffset GeneratedAt,
    string? AccountAlias = null);

/// <summary>
/// Política tipada do score e dos limiares de recomendação. Os defaults preservam a
/// semântica histórica; deployments podem ajustá-los em
/// <c>Harness:Governance:Evaluation:Scoring</c>.
/// </summary>
public sealed record EvaluationScoringOptions
{
    public double TestPassRateWeight { get; init; } = 0.60;

    public double OverallPassWeight { get; init; } = 0.40;

    public double FindingPenaltyP0 { get; init; } = 0.40;

    public double FindingPenaltyP1 { get; init; } = 0.20;

    public double FindingPenaltyP2 { get; init; } = 0.08;

    public double FindingPenaltyP3 { get; init; } = 0.02;

    public double HighPerformanceScoreThreshold { get; init; } = 0.85;

    public double HighPerformanceConfidenceLowerThreshold { get; init; } = 0.85;

    public double DegradedScoreThreshold { get; init; } = 0.50;

    public double DegradedConfidenceUpperThreshold { get; init; } = 0.50;

    public void Validate()
    {
        Probability(TestPassRateWeight, nameof(TestPassRateWeight));
        Probability(OverallPassWeight, nameof(OverallPassWeight));
        Probability(HighPerformanceScoreThreshold, nameof(HighPerformanceScoreThreshold));
        Probability(
            HighPerformanceConfidenceLowerThreshold,
            nameof(HighPerformanceConfidenceLowerThreshold));
        Probability(DegradedScoreThreshold, nameof(DegradedScoreThreshold));
        Probability(
            DegradedConfidenceUpperThreshold,
            nameof(DegradedConfidenceUpperThreshold));
        NonNegative(FindingPenaltyP0, nameof(FindingPenaltyP0));
        NonNegative(FindingPenaltyP1, nameof(FindingPenaltyP1));
        NonNegative(FindingPenaltyP2, nameof(FindingPenaltyP2));
        NonNegative(FindingPenaltyP3, nameof(FindingPenaltyP3));

        if (Math.Abs(TestPassRateWeight + OverallPassWeight - 1.0) > 0.000001)
        {
            throw new ArgumentException(
                "Evaluation base score weights must sum to 1.",
                nameof(EvaluationScoringOptions));
        }

        if (DegradedScoreThreshold > HighPerformanceScoreThreshold ||
            DegradedConfidenceUpperThreshold > HighPerformanceConfidenceLowerThreshold)
        {
            throw new ArgumentException(
                "Evaluation degraded thresholds cannot exceed high-performance thresholds.",
                nameof(EvaluationScoringOptions));
        }
    }

    private static void Probability(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(name, "Evaluation probability must be between 0 and 1.");
        }
    }

    private static void NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new ArgumentOutOfRangeException(name, "Evaluation penalty must be non-negative.");
        }
    }
}

/// <summary>
/// Contrato do Evaluation Service (Fase 5 / N5).
/// </summary>
public interface IEvaluationService
{
    double CalculateCompositeScore(
        double testPassRate,
        int findingCountP0,
        int findingCountP1,
        int findingCountP2,
        int findingCountP3,
        bool overallPass);

    (double Lower, double Upper) CalculateConfidenceInterval(
        int sampleSize,
        int successCount,
        double confidenceLevel = 0.95);

    IReadOnlyList<PerformanceAggregate> AggregatePerformance(
        IReadOnlyList<FreshContextEvaluationResult> evaluationResults,
        int minSampleSize = 5);

    IReadOnlyList<EvaluationRecommendation> GenerateRecommendations(
        IReadOnlyList<PerformanceAggregate> aggregates,
        DateTimeOffset now);
}

/// <summary>
/// Serviço de avaliação estatística e score composto com intervalo de confiança (Fase 5 / N5).
/// </summary>
public sealed class EvaluationService
    : IEvaluationService
{
    private const double DefaultZ95 = 1.96; // 95% de confiança z-score
    private readonly EvaluationScoringOptions _options;

    public EvaluationService(EvaluationScoringOptions? options = null)
    {
        _options = options ?? new EvaluationScoringOptions();
        _options.Validate();
    }

    public double CalculateCompositeScore(
        double testPassRate,
        int findingCountP0,
        int findingCountP1,
        int findingCountP2,
        int findingCountP3,
        bool overallPass)
    {
        var clampedPassRate = Math.Clamp(testPassRate, 0.0, 1.0);
        var baseScore =
            (clampedPassRate * _options.TestPassRateWeight) +
            (overallPass ? _options.OverallPassWeight : 0.0);

        var penaltyP0 = findingCountP0 * _options.FindingPenaltyP0;
        var penaltyP1 = findingCountP1 * _options.FindingPenaltyP1;
        var penaltyP2 = findingCountP2 * _options.FindingPenaltyP2;
        var penaltyP3 = findingCountP3 * _options.FindingPenaltyP3;

        var totalPenalty = penaltyP0 + penaltyP1 + penaltyP2 + penaltyP3;
        var finalScore = baseScore - totalPenalty;

        return Math.Round(Math.Clamp(finalScore, 0.0, 1.0), 4);
    }

    public (double Lower, double Upper) CalculateConfidenceInterval(
        int sampleSize,
        int successCount,
        double confidenceLevel = 0.95)
    {
        if (sampleSize <= 0)
        {
            return (0.0, 0.0);
        }

        var n = (double)sampleSize;
        var k = (double)Math.Clamp(successCount, 0, sampleSize);
        var p = k / n;

        var z = confidenceLevel switch
        {
            >= 0.99 => 2.576,
            >= 0.90 => 1.645,
            _ => DefaultZ95
        };

        var z2 = z * z;
        var denominator = 1.0 + (z2 / n);
        var center = (p + (z2 / (2.0 * n))) / denominator;

        var varianceTerm = (p * (1.0 - p) / n) + (z2 / (4.0 * n * n));
        var margin = (z / denominator) * Math.Sqrt(Math.Max(0.0, varianceTerm));

        var lower = Math.Round(Math.Clamp(center - margin, 0.0, 1.0), 4);
        var upper = Math.Round(Math.Clamp(center + margin, 0.0, 1.0), 4);

        return (lower, upper);
    }

    public IReadOnlyList<PerformanceAggregate> AggregatePerformance(
        IReadOnlyList<FreshContextEvaluationResult> evaluationResults,
        int minSampleSize = 5)
    {
        ArgumentNullException.ThrowIfNull(evaluationResults);

        if (evaluationResults.Count == 0)
        {
            return [];
        }

        var groups = evaluationResults
            .GroupBy(res => (
                TargetId: res.ProducerAgentId ?? "unknown",
                Model: res.Model ?? "unknown",
                Provider: res.Provider ?? "unknown",
                AccountAlias: res.AccountAlias,
                TaskSignature: res.TaskSignature))
            .ToList();

        var aggregates = new List<PerformanceAggregate>();

        foreach (var group in groups)
        {
            var total = group.Count();
            var passes = group.Count(res => res.Verdict == EvaluationVerdict.Pass);
            var fails = total - passes;
            var passRate = Math.Round((double)passes / total, 4);

            var scores = new List<double>();
            foreach (var res in group)
            {
                var p0 = res.Findings.Count(f => f.Priority == ReviewPriority.P0);
                var p1 = res.Findings.Count(f => f.Priority == ReviewPriority.P1);
                var p2 = res.Findings.Count(f => f.Priority == ReviewPriority.P2);
                var p3 = res.Findings.Count(f => f.Priority == ReviewPriority.P3);

                var score = CalculateCompositeScore(passRate, p0, p1, p2, p3, res.Verdict == EvaluationVerdict.Pass);
                scores.Add(score);
            }

            var avgCompositeScore = Math.Round(scores.Average(), 4);
            var (icLow, icHigh) = CalculateConfidenceInterval(total, passes);

            aggregates.Add(new PerformanceAggregate(
                TargetId: group.Key.TargetId,
                Model: group.Key.Model,
                Provider: group.Key.Provider,
                TaskSignature: group.Key.TaskSignature,
                SampleSize: total,
                SuccessCount: passes,
                FailureCount: fails,
                PassRate: passRate,
                CompositeScore: avgCompositeScore,
                ConfidenceIntervalLower: icLow,
                ConfidenceIntervalUpper: icHigh,
                SampleSizeQualified: total >= minSampleSize,
                AccountAlias: group.Key.AccountAlias));
        }

        return aggregates;
    }

    public IReadOnlyList<EvaluationRecommendation> GenerateRecommendations(
        IReadOnlyList<PerformanceAggregate> aggregates,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(aggregates);

        var recommendations = new List<EvaluationRecommendation>();

        foreach (var agg in aggregates)
        {
            if (!agg.SampleSizeQualified)
            {
                recommendations.Add(new EvaluationRecommendation(
                    TargetId: agg.TargetId,
                    Model: agg.Model,
                    Provider: agg.Provider,
                    TaskSignature: agg.TaskSignature,
                    Action: "insufficient_sample_size",
                    Score: agg.CompositeScore,
                    ConfidenceIntervalLower: agg.ConfidenceIntervalLower,
                    ConfidenceIntervalUpper: agg.ConfidenceIntervalUpper,
                    SampleSize: agg.SampleSize,
                    RecommendationReason: string.Format(CultureInfo.InvariantCulture, "sample_size_{0}_below_min_threshold", agg.SampleSize),
                    GeneratedAt: now,
                    AccountAlias: agg.AccountAlias));
                continue;
            }

            if (agg.ConfidenceIntervalLower >= _options.HighPerformanceConfidenceLowerThreshold &&
                agg.CompositeScore >= _options.HighPerformanceScoreThreshold)
            {
                recommendations.Add(new EvaluationRecommendation(
                    TargetId: agg.TargetId,
                    Model: agg.Model,
                    Provider: agg.Provider,
                    TaskSignature: agg.TaskSignature,
                    Action: "recommend_high_performance",
                    Score: agg.CompositeScore,
                    ConfidenceIntervalLower: agg.ConfidenceIntervalLower,
                    ConfidenceIntervalUpper: agg.ConfidenceIntervalUpper,
                    SampleSize: agg.SampleSize,
                    RecommendationReason: string.Format(CultureInfo.InvariantCulture, "ic_lower_{0}_exceeds_high_threshold", agg.ConfidenceIntervalLower),
                    GeneratedAt: now,
                    AccountAlias: agg.AccountAlias));
            }
            else if (agg.ConfidenceIntervalUpper <= _options.DegradedConfidenceUpperThreshold ||
                     agg.CompositeScore < _options.DegradedScoreThreshold)
            {
                recommendations.Add(new EvaluationRecommendation(
                    TargetId: agg.TargetId,
                    Model: agg.Model,
                    Provider: agg.Provider,
                    TaskSignature: agg.TaskSignature,
                    Action: "flag_degraded_performance",
                    Score: agg.CompositeScore,
                    ConfidenceIntervalLower: agg.ConfidenceIntervalLower,
                    ConfidenceIntervalUpper: agg.ConfidenceIntervalUpper,
                    SampleSize: agg.SampleSize,
                    RecommendationReason: string.Format(CultureInfo.InvariantCulture, "ic_upper_{0}_below_degraded_threshold", agg.ConfidenceIntervalUpper),
                    GeneratedAt: now,
                    AccountAlias: agg.AccountAlias));
            }
            else
            {
                recommendations.Add(new EvaluationRecommendation(
                    TargetId: agg.TargetId,
                    Model: agg.Model,
                    Provider: agg.Provider,
                    TaskSignature: agg.TaskSignature,
                    Action: "maintain_current_routing",
                    Score: agg.CompositeScore,
                    ConfidenceIntervalLower: agg.ConfidenceIntervalLower,
                    ConfidenceIntervalUpper: agg.ConfidenceIntervalUpper,
                    SampleSize: agg.SampleSize,
                    RecommendationReason: "performance_within_normal_operating_parameters",
                    GeneratedAt: now,
                    AccountAlias: agg.AccountAlias));
            }
        }

        return recommendations;
    }
}
