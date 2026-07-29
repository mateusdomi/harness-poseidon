using Harness.Modules.Delivery.Application;

namespace Harness.UnitTests.Delivery;

public sealed class DeliveryPortfolioProjectorTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DerivesTypedAttentionSignalsFromRecordedFacts()
    {
        var input = Build(
            tasks:
            [
                Task("t1", "d1", "blocked", "agent_task", assignee: "ag1",
                    blockedReason: "Aguardando acesso ao banco de dados de homologação"),
                Task("t2", "d1", "development", "decision", assignee: null,
                    blockedReason: null),
            ],
            demands: [Demand("d1")],
            solicitations: [Solicitation("s2", supersedes: "s1")],
            documents: [],
            stuckTaskCount: 1);

        var summary = DeliveryPortfolioProjector.Summarize(input);
        var codes = summary.AttentionSignals.Select(s => s.Code).ToHashSet();

        Assert.Contains("pending_db_access", codes);
        Assert.Contains("architectural_decision", codes);
        Assert.Contains("scope_change", codes);
        Assert.Contains("missing_doc", codes);
        Assert.Contains("execution_stuck", codes);
        Assert.Contains("no_owner", codes); // t2 ativa sem assignee
        Assert.Contains("dependency", codes); // t1 bloqueada por dependência
        Assert.True(DeliveryPortfolioProjector.NeedsAttention(summary));
        // Sinal crítico presente → saúde vermelha.
        Assert.Equal("red", summary.Health);
    }

    [Fact]
    public void HealthyDeliveryHasNoSignalsAndIsGreen()
    {
        var input = Build(
            tasks:
            [
                Task("t1", "d1", "done", "agent_task", assignee: "ag1", blockedReason: null,
                    dueAt: AsOf.AddDays(-1), updatedAt: AsOf.AddDays(-1)),
            ],
            demands: [Demand("d1")],
            solicitations: [Solicitation("s1", supersedes: null)],
            documents: [Doc("spec"), Doc("design"), Doc("runbook"), Doc("report")],
            stuckTaskCount: 0);

        var summary = DeliveryPortfolioProjector.Summarize(input);

        Assert.Empty(summary.AttentionSignals);
        Assert.Equal("green", summary.Health);
        Assert.False(DeliveryPortfolioProjector.NeedsAttention(summary));
        Assert.Equal(1, summary.MilestonesDone);
        Assert.Equal(1, summary.MilestonesTotal);
    }

    [Fact]
    public void NoRecentUpdateFlaggedWhenWorkIsStale()
    {
        var input = Build(
            tasks:
            [
                Task("t1", "d1", "development", "agent_task", assignee: "ag1", blockedReason: null,
                    dueAt: null, updatedAt: AsOf.AddDays(-30)),
            ],
            demands: [Demand("d1")],
            solicitations: [],
            documents: [Doc("spec"), Doc("design"), Doc("runbook"), Doc("report")],
            stuckTaskCount: 0,
            lastActivityAt: AsOf.AddDays(-30));

        var summary = DeliveryPortfolioProjector.Summarize(input);

        Assert.Contains(summary.AttentionSignals, s => s.Code == "no_recent_update");
    }

    // ---- builders --------------------------------------------------------------------------------

    private static DeliveryProjectionInput Build(
        IReadOnlyList<DeliveryTaskFacts> tasks,
        IReadOnlyList<DeliveryDemandFacts> demands,
        IReadOnlyList<DeliverySolicitationFacts> solicitations,
        IReadOnlyList<DeliveryDocumentFacts> documents,
        int stuckTaskCount,
        DateTimeOffset? lastActivityAt = null,
        DateTimeOffset? targetDeadline = null) =>
        new(
            new DeliveryProjectFacts(
                "p1", "Pagamentos", "PAY", "high", "chief1",
                AsOf.AddDays(-40), lastActivityAt ?? AsOf.AddHours(-1), targetDeadline),
            AsOf, solicitations, demands, tasks, [], documents, stuckTaskCount, [], []);

    [Fact]
    public void OwnerDeadlineAndStartReachTheTrackingView()
    {
        // Rastreamento de encomenda (D10): a tela precisa dizer quando comecou
        // e para quando o dono pediu, sem inventar nenhum dos dois.
        var deadline = AsOf.AddDays(15);
        var summary = DeliveryPortfolioProjector.Summarize(
            Build([], [], [], [], 0, targetDeadline: deadline));

        Assert.Equal(deadline, summary.TargetDeadline);
        Assert.Equal(AsOf.AddDays(-40), summary.StartedAt);
    }

    [Fact]
    public void ProjectWithoutDeadlineSaysNothingInsteadOfGuessing()
    {
        var summary = DeliveryPortfolioProjector.Summarize(Build([], [], [], [], 0));

        // Nulo e a resposta honesta: "o dono nao disse". A previsao calculada
        // vive em ForecastDate e nunca ocupa o lugar do prazo declarado.
        Assert.Null(summary.TargetDeadline);
    }

    private static DeliveryTaskFacts Task(
        string id, string demandId, string state, string cardType, string? assignee,
        string? blockedReason, DateTimeOffset? dueAt = null, DateTimeOffset? updatedAt = null) =>
        new(id, demandId, state, cardType, assignee, blockedReason, dueAt, updatedAt ?? AsOf.AddHours(-2));

    private static DeliveryDemandFacts Demand(string id) => new(id, "open", AsOf.AddDays(-10));

    private static DeliverySolicitationFacts Solicitation(string id, string? supersedes) =>
        new(id, "triaged", supersedes, AsOf.AddDays(-15));

    private static DeliveryDocumentFacts Doc(string kind) => new(kind, "published");
}
