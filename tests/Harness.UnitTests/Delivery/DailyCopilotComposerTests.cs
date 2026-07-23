using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;

namespace Harness.UnitTests.Delivery;

/// <summary>
/// DEL-03: o copiloto deriva o briefing dos fatos e resume as marcações capturadas, SEM criar cards de
/// PO. Briefing honesto (primeira daily × mudanças desde a última); resumo agrupa por tipo.
/// </summary>
public sealed class DailyCopilotComposerTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstDailyBriefingDerivesAttentionAndQuestionsFromSignals()
    {
        var input = Build(
            tasks:
            [
                Task("t1", "d1", "blocked", blockedReason: "aguardando acesso ao banco de dados"),
                Task("t2", "d1", "ready"),
            ],
            demands: [Demand("d1")],
            documents: []);

        var briefing = DailyCopilotComposer.Briefing(input, []);

        Assert.True(briefing.IsFirstDaily);
        Assert.Null(briefing.LastDailyAt);
        Assert.Contains(briefing.ChangesSinceLast, c => c.Code == "first_daily");

        // Sinais de atenção reusados do agregado; perguntas derivadas de sinais concretos.
        Assert.Contains(briefing.ItemsNeedingAttention, s => s.Code == "pending_db_access");
        Assert.Contains(briefing.RecommendedQuestions, q => q.Topic == "access");
        Assert.Contains(briefing.RecommendedQuestions, q => q.Topic == "doc"); // docs esperados ausentes
        Assert.Equal("red", briefing.Snapshot.Health); // sinal crítico
    }

    [Fact]
    public void BriefingReportsChangesSinceTheLastDaily()
    {
        var lastDaily = AsOf.AddDays(-1);
        var input = Build(
            tasks:
            [
                Task("t1", "d1", "development", updatedAt: AsOf.AddHours(-2)), // depois da última daily
                Task("t2", "d1", "ready", updatedAt: AsOf.AddDays(-3)), // antes
            ],
            demands: [Demand("d1")],
            documents: [Doc("spec"), Doc("design"), Doc("runbook"), Doc("report")]);

        var captures = new[]
        {
            new DeliveryDailyCaptureFacts("c1", DailyCaptureKinds.Risk, "risco X", "Mateus", lastDaily),
        };

        var briefing = DailyCopilotComposer.Briefing(input, captures);

        Assert.False(briefing.IsFirstDaily);
        Assert.Equal(lastDaily, briefing.LastDailyAt);
        Assert.Contains(briefing.ChangesSinceLast, c => c.Code == "tasks_updated");
        Assert.DoesNotContain(briefing.ChangesSinceLast, c => c.Code == "first_daily");
    }

    [Fact]
    public void SummaryGroupsCapturesByKindAndNeverCreatesPoCards()
    {
        var input = Build(
            tasks: [Task("t1", "d1", "ready")],
            demands: [Demand("d1")],
            documents: []);

        var captures = new[]
        {
            new DeliveryDailyCaptureFacts("c3", DailyCaptureKinds.Risk, "risco", "Ana", AsOf.AddMinutes(-1)),
            new DeliveryDailyCaptureFacts("c2", DailyCaptureKinds.Access, "acesso", "Ana", AsOf.AddMinutes(-5)),
            new DeliveryDailyCaptureFacts("c1", DailyCaptureKinds.Access, "acesso 2", "Ana", AsOf.AddMinutes(-9)),
        };

        var summary = DailyCopilotComposer.Summary(input, captures);

        Assert.False(summary.CreatedPoCards);
        Assert.Equal(3, summary.TotalCaptures);
        var byKind = summary.ByKind.ToDictionary(k => k.Kind, k => k.Count, StringComparer.Ordinal);
        Assert.Equal(2, byKind[DailyCaptureKinds.Access]);
        Assert.Equal(1, byKind[DailyCaptureKinds.Risk]);
        // Mais recente primeiro na lista completa.
        Assert.Equal("c3", summary.Captures[0].Id);
        Assert.Equal(AsOf.AddMinutes(-9), summary.SessionSince);
    }

    [Fact]
    public void KindValidationAcceptsOnlyTheTypedMarkings()
    {
        Assert.True(DailyCaptureKinds.IsValid("decision"));
        Assert.False(DailyCaptureKinds.IsValid("random"));
        Assert.False(DailyCaptureKinds.IsValid(null));
        Assert.Equal(7, DailyCaptureKinds.All.Count);
    }

    // ---- builders --------------------------------------------------------------------------------

    private static DeliveryProjectionInput Build(
        IReadOnlyList<DeliveryTaskFacts> tasks,
        IReadOnlyList<DeliveryDemandFacts> demands,
        IReadOnlyList<DeliveryDocumentFacts> documents) =>
        new(
            new DeliveryProjectFacts("p1", "Pagamentos", "PAY", "high", "chief1",
                AsOf.AddDays(-40), AsOf.AddHours(-1)),
            AsOf, [], demands, tasks, [], documents, 0, [], []);

    private static DeliveryTaskFacts Task(
        string id, string demandId, string state,
        string? blockedReason = null, DateTimeOffset? updatedAt = null) =>
        new(id, demandId, state, "agent_task", "ag1", blockedReason, null, updatedAt ?? AsOf.AddHours(-2));

    private static DeliveryDemandFacts Demand(string id) => new(id, "open", AsOf.AddDays(-10));

    private static DeliveryDocumentFacts Doc(string kind) => new(kind, "published");
}
