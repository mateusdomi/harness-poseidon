using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;

namespace Harness.UnitTests.Delivery;

/// <summary>
/// DEL-04/DEL-05 — o compositor deriva cada tipo de relatório dos MESMOS fatos do 360/métricas/previsão,
/// de forma determinística e com as seções esperadas. Nada é inventado; dois compositores sobre o mesmo
/// input + instante produzem documentos idênticos.
/// </summary>
public sealed class DeliveryReportComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Committed = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static DeliveryProjectionInput SeededInput()
    {
        var created = Now.AddDays(-40);
        var tasks = new[]
        {
            new DeliveryTaskFacts("T1", "D1", "done", "agent_task", "agent-1", null, Committed.AddDays(-5), Now.AddDays(-3)),
            new DeliveryTaskFacts("T2", "D1", "development", "agent_task", "agent-1", null, Committed, Now.AddDays(-1)),
            new DeliveryTaskFacts("T3", "D2", "blocked", "agent_task", null,
                "Aguardando acesso ao banco de dados de homologação", Committed, Now.AddDays(-2)),
            new DeliveryTaskFacts("T4", "D2", "review", "decision", "agent-2",
                "decisão arquitetural pendente", null, Now.AddDays(-1)),
        };
        var attempts = new[]
        {
            new DeliveryAttemptFacts("T1", 1, "approved", null, "h1"),
            new DeliveryAttemptFacts("T2", 1, "rejected", "timeout", "h2"),
            new DeliveryAttemptFacts("T2", 2, "in_progress", null, "h3"),
        };
        return new DeliveryProjectionInput(
            new DeliveryProjectFacts("01H0000000000000000000DLV1", "Pagamentos", "PAY", "high", "chief-1", created, Now.AddDays(-1)),
            Now,
            [new DeliverySolicitationFacts("S1", "accepted", null, created), new DeliverySolicitationFacts("S2", "accepted", "S1", created.AddDays(2))],
            [new DeliveryDemandFacts("D1", "active", created), new DeliveryDemandFacts("D2", "active", created.AddDays(1))],
            tasks,
            attempts,
            [new DeliveryDocumentFacts("spec", "approved"), new DeliveryDocumentFacts("design", "approved")],
            StuckTaskCount: 0,
            [new DeliveryFeatureMetricFacts("F1", 2, 3, 1, 1, 1.25m, 100, 200, 5000)],
            ForecastHistory: []);
    }

    private static DeliveryReportSpec Spec(DeliveryReportType type) =>
        new(type, "coordination", "internal", Now);

    [Theory]
    [InlineData(DeliveryReportType.WeeklyExecutiveStatus)]
    [InlineData(DeliveryReportType.MilestoneReport)]
    [InlineData(DeliveryReportType.HomologationReadiness)]
    [InlineData(DeliveryReportType.ProductionReadiness)]
    [InlineData(DeliveryReportType.ClosureDossier)]
    public void EveryTypeComposesTitledDocumentWithExecutiveSummary(DeliveryReportType type)
    {
        var input = SeededInput();
        var document = DeliveryReportComposer.Compose(Spec(type), input);

        Assert.Equal(DeliveryReportTokens.ToToken(type), document.Type);
        Assert.Equal("PAY", document.ProjectKey);
        Assert.Equal("01H0000000000000000000DLV1", document.DeliveryId);
        Assert.Equal("coordination", document.Audience);
        Assert.Equal(Now, document.GeneratedAt);
        Assert.NotEmpty(document.Sections);
        Assert.Contains(document.Sections, s => s.Key == "executive_summary");
    }

    [Fact]
    public void CompositionIsDeterministic()
    {
        var input = SeededInput();
        var first = DeliveryReportComposer.Compose(Spec(DeliveryReportType.WeeklyExecutiveStatus), input);
        var second = DeliveryReportComposer.Compose(Spec(DeliveryReportType.WeeklyExecutiveStatus), input);

        var json = new JsonReportRenderer();
        Assert.Equal(json.Render(first).Content, json.Render(second).Content);
    }

    [Fact]
    public void WeeklyStatusSurfacesAttentionAndForecastDerivedFromFacts()
    {
        var document = DeliveryReportComposer.Compose(Spec(DeliveryReportType.WeeklyExecutiveStatus), SeededInput());

        var attention = Assert.Single(document.Sections, s => s.Key == "attention");
        Assert.NotNull(attention.Table);
        // Sinal crítico de acesso a banco veio do BlockedReason da task T3.
        Assert.Contains(attention.Table!.Rows, row => row.Contains("pending_db_access"));
        Assert.Contains(document.Sections, s => s.Key == "forecast");
    }

    [Fact]
    public void ProductionReadinessIsStricterThanHomologation()
    {
        var input = SeededInput();
        var homolog = DeliveryReportComposer.Compose(Spec(DeliveryReportType.HomologationReadiness), input);
        var prod = DeliveryReportComposer.Compose(Spec(DeliveryReportType.ProductionReadiness), input);

        var homologReadiness = Assert.Single(homolog.Sections, s => s.Key == "readiness");
        var prodReadiness = Assert.Single(prod.Sections, s => s.Key == "readiness");
        // Produção adiciona critérios (marcos concluídos, runbook, sem críticos): mais linhas.
        Assert.True(prodReadiness.Table!.Rows.Count > homologReadiness.Table!.Rows.Count);
        // A entrega tem uma task bloqueada → não está pronta em nenhum dos dois.
        Assert.Equal("não pronto", prodReadiness.Fields.Single(f => f.Label == "Veredito").Value);
    }

    [Fact]
    public void ClosureDossierCapturesTheDel05Sections()
    {
        var document = DeliveryReportComposer.Compose(Spec(DeliveryReportType.ClosureDossier), SeededInput());

        var keys = document.Sections.Select(s => s.Key).ToHashSet();
        foreach (var expected in new[]
        {
            "solicited_delivered", "architecture", "elapsed_time", "decisions", "value_metrics",
            "defects", "value", "residual_risks", "lessons", "support",
        })
        {
            Assert.Contains(expected, keys);
        }

        var solicited = document.Sections.Single(s => s.Key == "solicited_delivered");
        // Duas solicitações, uma substituição (mudança de escopo) — derivado dos fatos.
        Assert.Equal("2", solicited.Fields.Single(f => f.Label == "Solicitações").Value);
        Assert.Equal("1", solicited.Fields.Single(f => f.Label == "Mudanças de escopo").Value);

        var defects = document.Sections.Single(s => s.Key == "defects");
        // A tentativa rejeitada de T2 conta como defeito/retrabalho.
        Assert.Equal("1", defects.Fields.Single(f => f.Label == "Tasks com retrabalho").Value);
    }
}
