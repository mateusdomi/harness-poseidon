using System.Globalization;
using Harness.Modules.Delivery.Contracts;

namespace Harness.Modules.Delivery.Application;

/// <summary>
/// DEL-02 — Projeto 360. Agrega PURA e deterministicamente a entrega em abas: Resumo executivo,
/// Plano&amp;marcos (com histórico de previsão — nunca sobrescrita), Saúde técnica (10 indicadores),
/// Riscos&amp;dependências, Decisões, Documentação (checklist esperado × presente) e Valor&amp;métricas
/// (reusa as feature-metrics do PLAT-04). Todo indicador deriva de dados reais.
/// </summary>
public static class Delivery360Projector
{
    public static Delivery360Contract Project(DeliveryProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var aggregate = DeliveryFactsCalculator.Compute(input);
        var health = DeliveryFactsCalculator.Health(aggregate.AttentionSignals);
        var predictability = DeliveryFactsCalculator.Predictability(aggregate);

        return new Delivery360Contract(
            DeliveryId: input.Project.ProjectId,
            ProjectId: input.Project.ProjectId,
            ExecutiveSummary: ExecutiveSummary(input, aggregate, health, predictability),
            PlanAndMilestones: PlanAndMilestones(input, aggregate),
            TechnicalHealth: TechnicalHealth(input, aggregate),
            RisksAndDependencies: new DeliveryRiskDependencyContract(
                aggregate.AttentionSignals, aggregate.OpenDependencies, aggregate.BlockedTaskCount),
            Decisions: Decisions(input),
            Documentation: Documentation(input),
            ValueAndMetrics: ValueAndMetrics(input));
    }

    private static DeliveryExecutiveSummaryContract ExecutiveSummary(
        DeliveryProjectionInput input, DeliveryAggregate aggregate, string health, string predictability) =>
        new(
            input.Project.Name, input.Project.Key, input.Project.Criticality, health, predictability,
            aggregate.Owner, aggregate.MilestonesTotal, aggregate.MilestonesDone,
            aggregate.OpenTaskCount, aggregate.BlockedTaskCount, aggregate.CommittedDate,
            aggregate.Forecast.ForecastDate, aggregate.LastActivityAt,
            aggregate.AttentionSignals.Count);

    private static DeliveryPlanMilestonesContract PlanAndMilestones(
        DeliveryProjectionInput input, DeliveryAggregate aggregate)
    {
        var live = ToContract(id: null, aggregate.Forecast, createdAt: null);
        var history = input.ForecastHistory
            .Select(f => new DeliveryForecastContract(
                f.Id, f.ForecastDate, f.Confidence, f.ConfidencePercent, f.HasSufficientEvidence,
                f.Basis.Select(b => new ForecastBasisContract(b.Signal, b.Detail)).ToArray(),
                f.CreatedAt))
            .ToArray();
        return new DeliveryPlanMilestonesContract(
            aggregate.MilestonesTotal, aggregate.MilestonesDone, aggregate.CommittedDate, live, history);
    }

    private static DeliveryTechnicalHealthContract TechnicalHealth(
        DeliveryProjectionInput input, DeliveryAggregate aggregate)
    {
        var tasks = input.Tasks;
        var attempts = input.Attempts;
        var doneCount = tasks.Count(DeliveryFactsCalculator.IsDone);
        var totalTasks = tasks.Count;
        var approved = attempts.Count(a => string.Equals(a.State, "approved", StringComparison.OrdinalIgnoreCase));
        var rejected = attempts.Count(a => string.Equals(a.State, "rejected", StringComparison.OrdinalIgnoreCase));
        var totalAttempts = attempts.Count;
        var failedAttempts = attempts.Count(a =>
            string.Equals(a.State, "rejected", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(a.FailureReason));
        var reworkTasks = attempts
            .GroupBy(a => a.TaskId, StringComparer.Ordinal)
            .Count(g => g.Count() > 1);
        var missingDocs = DeliveryFactsCalculator.MissingDocKinds(input.Documents).Count;
        var docExpected = DeliveryFactsCalculator.ExpectedDocs.Count;

        var indicators = new List<TechnicalHealthIndicatorContract>
        {
            Indicator("task_completion", "Conclusão de tasks",
                Ratio(doneCount, totalTasks), $"{doneCount}/{totalTasks} tasks done",
                Grade(totalTasks == 0 ? (double?)null : doneCount / (double)totalTasks, 0.8, 0.4)),
            Indicator("attempt_success_rate", "Taxa de sucesso de tentativas",
                Ratio(approved, totalAttempts), $"{approved}/{totalAttempts} attempts approved",
                Grade(totalAttempts == 0 ? (double?)null : approved / (double)totalAttempts, 0.7, 0.4)),
            Indicator("blocked_tasks", "Tasks bloqueadas",
                aggregate.BlockedTaskCount.ToString(CultureInfo.InvariantCulture),
                $"{aggregate.BlockedTaskCount} blocked", InverseCount(aggregate.BlockedTaskCount, 1, 3)),
            Indicator("stuck_executions", "Execuções travadas (PLAT-04)",
                input.StuckTaskCount.ToString(CultureInfo.InvariantCulture),
                $"{input.StuckTaskCount} stuck", InverseCount(input.StuckTaskCount, 1, 1)),
            Indicator("rework", "Retrabalho (tasks com múltiplas tentativas)",
                reworkTasks.ToString(CultureInfo.InvariantCulture),
                $"{reworkTasks} task(s) retried", InverseCount(reworkTasks, 2, 4)),
            Indicator("failed_attempts", "Tentativas falhas",
                failedAttempts.ToString(CultureInfo.InvariantCulture),
                $"{failedAttempts} of {totalAttempts} attempts failed", InverseCount(failedAttempts, 1, 4)),
            Indicator("open_dependencies", "Dependências abertas",
                aggregate.OpenDependencies.ToString(CultureInfo.InvariantCulture),
                $"{aggregate.OpenDependencies} unresolved", InverseCount(aggregate.OpenDependencies, 1, 3)),
            Indicator("pending_validations", "Validações pendentes",
                aggregate.PendingValidations.ToString(CultureInfo.InvariantCulture),
                $"{aggregate.PendingValidations} awaiting review", InverseCount(aggregate.PendingValidations, 2, 5)),
            Indicator("documentation_coverage", "Cobertura de documentação",
                $"{docExpected - missingDocs}/{docExpected}",
                $"{docExpected - missingDocs} of {docExpected} expected docs present",
                Grade(docExpected == 0 ? (double?)null : (docExpected - missingDocs) / (double)docExpected, 0.75, 0.5)),
            Indicator("forecast_variance", "Variação de previsão",
                aggregate.AverageVarianceDays is { } v
                    ? v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)
                    : "n/a",
                aggregate.AverageVarianceDays is null
                    ? "no completed-milestone variance history"
                    : $"average {aggregate.AverageVarianceDays.Value:+0.#;-0.#;0} day variance",
                aggregate.AverageVarianceDays is null
                    ? "unknown"
                    : Math.Abs(aggregate.AverageVarianceDays.Value) <= 2
                        ? "good"
                        : Math.Abs(aggregate.AverageVarianceDays.Value) <= 5 ? "watch" : "bad"),
        };

        return new DeliveryTechnicalHealthContract(indicators);
    }

    private static DeliveryDecisionsContract Decisions(DeliveryProjectionInput input)
    {
        var decisionTasks = input.Tasks
            .Where(t => string.Equals(t.CardType, "decision", StringComparison.Ordinal))
            .ToArray();
        var decisions = decisionTasks
            .Select(t => new DeliveryDecisionContract(
                t.Id, t.State,
                t.BlockedReason ?? "Decision card",
                DeliveryFactsCalculator.IsDone(t)))
            .ToArray();
        var open = decisions.Count(d => !d.Resolved);
        return new DeliveryDecisionsContract(decisions.Length, open, decisions);
    }

    private static DeliveryDocumentationContract Documentation(DeliveryProjectionInput input)
    {
        var byKind = input.Documents
            .GroupBy(d => d.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().State, StringComparer.Ordinal);
        var checklist = DeliveryFactsCalculator.ExpectedDocs
            .Select(expected =>
            {
                var present = byKind.TryGetValue(expected.Kind, out var state);
                return new DocumentationChecklistItemContract(
                    expected.Kind, expected.Label, present, present ? state : null);
            })
            .ToArray();
        var presentCount = checklist.Count(c => c.Present);
        return new DeliveryDocumentationContract(checklist.Length, presentCount, checklist);
    }

    private static DeliveryValueMetricsContract ValueAndMetrics(DeliveryProjectionInput input)
    {
        var features = input.FeatureMetrics
            .Select(f => new DeliveryFeatureMetricContract(
                f.FeatureId, f.TaskCount, f.AttemptCount, f.SuccessCount, f.FailureCount,
                f.TotalCostUsd, f.TotalTokensInput, f.TotalTokensOutput, f.TotalDurationMs))
            .ToArray();
        var totalCost = features.Sum(f => f.TotalCostUsd);
        var totalTasks = features.Sum(f => f.TaskCount);
        return new DeliveryValueMetricsContract(features, totalCost, totalTasks);
    }

    private static DeliveryForecastContract ToContract(
        string? id, ForecastResult forecast, DateTimeOffset? createdAt) =>
        new(
            id, forecast.ForecastDate, DeliveryForecaster.ToConfidenceString(forecast.Confidence),
            forecast.ConfidencePercent, forecast.HasSufficientEvidence,
            forecast.Basis.Select(b => new ForecastBasisContract(b.Signal, b.Detail)).ToArray(),
            createdAt);

    private static TechnicalHealthIndicatorContract Indicator(
        string key, string label, string value, string detail, string status) =>
        new(key, label, status, value, detail);

    private static string Ratio(int numerator, int denominator) =>
        denominator == 0 ? "n/a" : $"{numerator}/{denominator}";

    // Maior = melhor: good acima de goodAt, bad abaixo de badAt, watch no meio, unknown se nulo.
    private static string Grade(double? value, double goodAt, double badAt) => value switch
    {
        null => "unknown",
        var v when v >= goodAt => "good",
        var v when v < badAt => "bad",
        _ => "watch",
    };

    // Menor = melhor (contagens de problemas): good em 0, watch até watchAt, bad a partir de badAt.
    private static string InverseCount(int count, int watchAt, int badAt) => count switch
    {
        0 => "good",
        var c when c >= badAt => "bad",
        var c when c >= watchAt => "watch",
        _ => "good",
    };
}
