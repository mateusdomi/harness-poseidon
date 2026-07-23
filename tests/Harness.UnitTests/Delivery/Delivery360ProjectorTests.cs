using Harness.Modules.Delivery.Application;

namespace Harness.UnitTests.Delivery;

public sealed class Delivery360ProjectorTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TechnicalHealthComputesTenIndicatorsFromRealData()
    {
        var input = Build(
            tasks:
            [
                Task("t1", "d1", "done", "agent_task", dueAt: AsOf.AddDays(-2), updatedAt: AsOf.AddDays(-1)),
                Task("t2", "d1", "review", "agent_task"),
                Task("t3", "d2", "blocked", "agent_task", blockedReason: "aguardando dependência externa"),
            ],
            demands: [Demand("d1"), Demand("d2")],
            documents: [Doc("spec", "published"), Doc("design", "draft")],
            attempts:
            [
                new DeliveryAttemptFacts("t1", 1, "approved", null, null),
                new DeliveryAttemptFacts("t2", 1, "rejected", "compile error", "h1"),
                new DeliveryAttemptFacts("t2", 2, "rejected", "compile error", "h1"),
            ],
            featureMetrics:
            [
                new DeliveryFeatureMetricFacts("PAY-01", 3, 3, 1, 2, 4.50m, 1000, 2000, 12345),
            ],
            stuckTaskCount: 1);

        var overview = Delivery360Projector.Project(input);
        var indicators = overview.TechnicalHealth.Indicators;

        Assert.Equal(10, indicators.Count);
        var byKey = indicators.ToDictionary(i => i.Key, StringComparer.Ordinal);
        Assert.Equal("1/3", byKey["task_completion"].Value);
        Assert.Equal("1/3", byKey["attempt_success_rate"].Value);
        Assert.Equal("1", byKey["blocked_tasks"].Value);
        Assert.Equal("1", byKey["stuck_executions"].Value);
        Assert.Equal("bad", byKey["stuck_executions"].Status);
        Assert.Equal("1", byKey["rework"].Value); // t2 teve 2 tentativas
        Assert.Equal("1", byKey["pending_validations"].Value); // t2 em review
        Assert.Equal("2/4", byKey["documentation_coverage"].Value); // spec+design presentes, faltam runbook+report

        // Valor & métricas reusa PLAT-04.
        Assert.Single(overview.ValueAndMetrics.Features);
        Assert.Equal(4.50m, overview.ValueAndMetrics.TotalCostUsd);

        // Documentação: checklist esperado × presente.
        Assert.Equal(4, overview.Documentation.Expected);
        Assert.Equal(2, overview.Documentation.Present);
        Assert.Contains(overview.Documentation.Checklist, c => c.Kind == "runbook" && !c.Present);
        Assert.Contains(overview.Documentation.Checklist, c => c.Kind == "spec" && c.Present);
    }

    [Fact]
    public void PlanTabExposesLiveForecastAndAppendOnlyHistory()
    {
        var history = new[]
        {
            new DeliveryStoredForecast("f2", AsOf.AddDays(30), "medium", 60, true,
                [new DeliveryStoredForecastBasis("committed_date", "committed date 2026-08-22")], AsOf.AddDays(-1)),
            new DeliveryStoredForecast("f1", AsOf.AddDays(28), "low", 30, true,
                [new DeliveryStoredForecastBasis("committed_date", "committed date 2026-08-20")], AsOf.AddDays(-3)),
        };
        var input = Build(
            tasks: [Task("t1", "d1", "done", "agent_task", dueAt: AsOf.AddDays(20), updatedAt: AsOf.AddDays(19))],
            demands: [Demand("d1")],
            documents: [],
            attempts: [],
            featureMetrics: [],
            stuckTaskCount: 0,
            forecastHistory: history);

        var overview = Delivery360Projector.Project(input);

        // Histórico preservado na ordem recebida (mais recente primeiro) — nunca sobrescrito.
        Assert.Equal(2, overview.PlanAndMilestones.ForecastHistory.Count);
        Assert.Equal("f2", overview.PlanAndMilestones.ForecastHistory[0].Id);
        Assert.Equal("f1", overview.PlanAndMilestones.ForecastHistory[1].Id);
        // A previsão viva é recomputada do estado atual (sem id ainda persistido).
        Assert.Null(overview.PlanAndMilestones.Forecast.Id);
        Assert.NotEmpty(overview.PlanAndMilestones.Forecast.Basis);
    }

    // ---- builders --------------------------------------------------------------------------------

    private static DeliveryProjectionInput Build(
        IReadOnlyList<DeliveryTaskFacts> tasks,
        IReadOnlyList<DeliveryDemandFacts> demands,
        IReadOnlyList<DeliveryDocumentFacts> documents,
        IReadOnlyList<DeliveryAttemptFacts> attempts,
        IReadOnlyList<DeliveryFeatureMetricFacts> featureMetrics,
        int stuckTaskCount,
        IReadOnlyList<DeliveryStoredForecast>? forecastHistory = null) =>
        new(
            new DeliveryProjectFacts("p1", "Pagamentos", "PAY", "high", "chief1",
                AsOf.AddDays(-40), AsOf.AddHours(-1)),
            AsOf, [], demands, tasks, attempts, documents, stuckTaskCount, featureMetrics,
            forecastHistory ?? []);

    private static DeliveryTaskFacts Task(
        string id, string demandId, string state, string cardType,
        string? blockedReason = null, DateTimeOffset? dueAt = null, DateTimeOffset? updatedAt = null) =>
        new(id, demandId, state, cardType, "ag1", blockedReason, dueAt, updatedAt ?? AsOf.AddHours(-2));

    private static DeliveryDemandFacts Demand(string id) => new(id, "open", AsOf.AddDays(-10));

    private static DeliveryDocumentFacts Doc(string kind, string state) => new(kind, state);
}
