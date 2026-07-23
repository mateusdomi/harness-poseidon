using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;

namespace Harness.UnitTests.Delivery;

/// <summary>
/// DEL-06: as métricas derivam ESTRITAMENTE de dados gravados. Onde o insumo existe, há valor; onde
/// não existe (carimbos de deploy), a métrica é "não medida" — nunca um número fabricado. Determinística.
/// </summary>
public sealed class DeliveryMetricsCalculatorTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DerivesRecordedMetricsAndMarksUnrecordedAsNotMeasured()
    {
        var input = Build(
            tasks:
            [
                Task("t1", "d1", "done", dueAt: AsOf.AddDays(-2), updatedAt: AsOf.AddDays(-1)),
                Task("t2", "d1", "corrections"),
                Task("t3", "d2", "blocked", blockedReason: "aguardando acesso ao banco de dados"),
            ],
            demands: [Demand("d1"), Demand("d2")],
            documents: [Doc("spec"), Doc("design")],
            attempts:
            [
                new DeliveryAttemptFacts("t1", 1, "approved", null, null),
                new DeliveryAttemptFacts("t2", 1, "rejected", "compile error", "h1"),
                new DeliveryAttemptFacts("t2", 2, "rejected", "compile error", "h1"),
            ],
            solicitations:
            [
                new DeliverySolicitationFacts("s1", "open", null, AsOf.AddDays(-9)),
                new DeliverySolicitationFacts("s2", "open", "s1", AsOf.AddDays(-3)),
            ]);

        var metrics = DeliveryMetricsCalculator.Compute(input);
        var dora = metrics.Dora.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var own = metrics.Own.ToDictionary(m => m.Key, StringComparer.Ordinal);

        // Insumos de deploy NÃO registrados ⇒ não medidas (sem valor fabricado).
        foreach (var key in new[] { "change_lead_time", "deployment_frequency", "failed_deploy_recovery" })
        {
            Assert.False(dora[key].Measured);
            Assert.Null(dora[key].Value);
            Assert.False(string.IsNullOrWhiteSpace(dora[key].Basis));
        }

        // Deriváveis de contagens de tentativas registradas ⇒ medidas.
        Assert.True(dora["change_fail_rate"].Measured);
        Assert.Equal("66.7", dora["change_fail_rate"].Value); // 2 de 3 tentativas falharam
        Assert.True(dora["deploy_rework"].Measured);
        Assert.Equal("50", dora["deploy_rework"].Value); // 1 de 2 tasks com tentativa teve retrabalho

        // Métricas próprias derivadas dos fatos.
        Assert.Equal("50", own["documentation_coverage"].Value); // 2 de 4 docs esperados
        Assert.Equal("1", own["homologation_defects"].Value); // t2 em corrections
        Assert.Equal("1", own["scope_changes"].Value); // s2 supersede s1
        Assert.Equal("1", own["open_dependencies"].Value); // t3 aguardando dependência (banco)
        Assert.Equal("1", own["time_waiting_access"].Value); // t3 bloqueada em acesso
        Assert.Equal("0/2", own["planned_vs_realized_value"].Value); // nenhum marco 100% concluído
        Assert.False(own["forecast_accuracy"].Measured); // sem entrega concluída
    }

    [Fact]
    public void WithoutAttemptsChangeMetricsAreNotMeasured()
    {
        var input = Build(
            tasks: [Task("t1", "d1", "ready")],
            demands: [Demand("d1")],
            documents: [],
            attempts: [],
            solicitations: []);

        var metrics = DeliveryMetricsCalculator.Compute(input);
        var dora = metrics.Dora.ToDictionary(m => m.Key, StringComparer.Ordinal);

        Assert.False(dora["change_fail_rate"].Measured);
        Assert.False(dora["deploy_rework"].Measured);
        Assert.All(metrics.Dora, m => Assert.False(m.Measured));
    }

    [Fact]
    public void ForecastAccuracyBecomesMeasuredForACompletedDelivery()
    {
        var forecast = new DeliveryStoredForecast(
            "f1", AsOf.AddDays(-3), "medium", 60, true,
            [new DeliveryStoredForecastBasis("committed_date", "committed date")], AsOf.AddDays(-10));
        var input = Build(
            tasks: [Task("t1", "d1", "done", dueAt: AsOf.AddDays(-3), updatedAt: AsOf.AddDays(-1))],
            demands: [Demand("d1")],
            documents: [],
            attempts: [],
            solicitations: [],
            forecastHistory: [forecast]);

        var metrics = DeliveryMetricsCalculator.Compute(input);
        var accuracy = metrics.Own.Single(m => m.Key == "forecast_accuracy");

        Assert.True(accuracy.Measured); // marco concluído + previsão com data ⇒ mensurável
        Assert.Equal("days", accuracy.Unit);
        Assert.Equal("+2", accuracy.Value); // realizado 2 dias após a previsão
    }

    [Fact]
    public void IsDeterministic()
    {
        var input = Build(
            tasks: [Task("t1", "d1", "corrections")],
            demands: [Demand("d1")],
            documents: [Doc("spec")],
            attempts: [new DeliveryAttemptFacts("t1", 1, "rejected", "x", "h")],
            solicitations: []);

        var first = DeliveryMetricsCalculator.Compute(input);
        var second = DeliveryMetricsCalculator.Compute(input);
        Assert.Equal(
            first.Dora.Select(m => (m.Key, m.Measured, m.Value)),
            second.Dora.Select(m => (m.Key, m.Measured, m.Value)));
        Assert.Equal(
            first.Own.Select(m => (m.Key, m.Measured, m.Value)),
            second.Own.Select(m => (m.Key, m.Measured, m.Value)));
    }

    // ---- builders --------------------------------------------------------------------------------

    private static DeliveryProjectionInput Build(
        IReadOnlyList<DeliveryTaskFacts> tasks,
        IReadOnlyList<DeliveryDemandFacts> demands,
        IReadOnlyList<DeliveryDocumentFacts> documents,
        IReadOnlyList<DeliveryAttemptFacts> attempts,
        IReadOnlyList<DeliverySolicitationFacts> solicitations,
        IReadOnlyList<DeliveryStoredForecast>? forecastHistory = null) =>
        new(
            new DeliveryProjectFacts("p1", "Pagamentos", "PAY", "high", "chief1",
                AsOf.AddDays(-40), AsOf.AddHours(-1)),
            AsOf, solicitations, demands, tasks, attempts, documents, 0, [],
            forecastHistory ?? []);

    private static DeliveryTaskFacts Task(
        string id, string demandId, string state,
        string? blockedReason = null, DateTimeOffset? dueAt = null, DateTimeOffset? updatedAt = null) =>
        new(id, demandId, state, "agent_task", "ag1", blockedReason, dueAt, updatedAt ?? AsOf.AddHours(-2));

    private static DeliveryDemandFacts Demand(string id) => new(id, "open", AsOf.AddDays(-10));

    private static DeliveryDocumentFacts Doc(string kind) => new(kind, "published");
}
