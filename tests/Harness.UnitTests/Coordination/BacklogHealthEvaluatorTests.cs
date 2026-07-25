using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// RN-04 — prova a detecção pura de cards presos (stuck): trabalho ativo além do limite de tempo
/// sem progresso é sinalizado com motivo tipado; o que é recente, passivo ou arquivado NUNCA é.
/// </summary>
public sealed class BacklogHealthEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(120);

    [Fact]
    public void ACardInDevelopmentBeyondTheThresholdIsStuck()
    {
        var stuck = BacklogHealthEvaluator.Evaluate(
            [Card("dev-card", "development", "ready", Now.AddHours(-3))], Now, Threshold);

        var card = Assert.Single(stuck);
        Assert.Equal("dev-card", card.TaskId);
        Assert.Equal(BacklogHealthEvaluator.StuckInProgress, card.ReasonCode);
        Assert.Equal(180, card.StuckForMinutes);
    }

    [Fact]
    public void ACardWithARunningAttemptBeyondTheThresholdIsStuckAsRunning()
    {
        // Mesmo em coluna passiva de board, uma tentativa em curso presa é sinalizada — é o caso
        // mais perigoso (o agente pode ter morrido sem liberar o claim).
        var stuck = BacklogHealthEvaluator.Evaluate(
            [Card("run-card", "ready", "running", Now.AddHours(-4))], Now, Threshold);

        var card = Assert.Single(stuck);
        Assert.Equal(BacklogHealthEvaluator.StuckRunning, card.ReasonCode);
    }

    [Fact]
    public void ARecentlyMovedActiveCardIsNotStuck()
    {
        var stuck = BacklogHealthEvaluator.Evaluate(
            [Card("fresh", "development", "ready", Now.AddMinutes(-10))], Now, Threshold);

        Assert.Empty(stuck);
    }

    [Fact]
    public void PassiveColumnsAreNeverStuck()
    {
        var stuck = BacklogHealthEvaluator.Evaluate(
            [
                Card("backlog", "backlog", "ready", Now.AddDays(-9)),
                Card("ready", "ready", "ready", Now.AddDays(-9)),
                Card("done", "done", "completed", Now.AddDays(-9)),
            ],
            Now, Threshold);

        Assert.Empty(stuck);
    }

    [Fact]
    public void AnArchivedCardIsNeverStuck()
    {
        var stuck = BacklogHealthEvaluator.Evaluate(
            [Card("archived", "development", "running", Now.AddDays(-9), archived: true)],
            Now, Threshold);

        Assert.Empty(stuck);
    }

    [Fact]
    public void StuckCardsAreOrderedByMostStuckFirst()
    {
        var stuck = BacklogHealthEvaluator.Evaluate(
            [
                Card("younger", "development", "ready", Now.AddHours(-3)),
                Card("older", "review", "ready", Now.AddHours(-9)),
            ],
            Now, Threshold);

        Assert.Collection(
            stuck,
            first => Assert.Equal("older", first.TaskId),
            second => Assert.Equal("younger", second.TaskId));
    }

    private static BacklogCardFacts Card(
        string id, string boardState, string internalState, DateTimeOffset updatedAt,
        bool archived = false) =>
        new(id, $"Card {id}", boardState, internalState, archived, updatedAt);
}

public sealed class BoardStateReconciliationEvaluatorTests
{
    [Fact]
    public void CompletedTaskIsDeterministicallyMovedToDone()
    {
        var decision = BoardStateReconciliationEvaluator.Evaluate(
            Facts("review", "completed", ["approved"]));

        Assert.NotNull(decision);
        Assert.Equal(BoardStateReconciliationEvaluator.Correction, decision.Kind);
        Assert.Equal("done", decision.TargetState);
    }

    [Fact]
    public void AwaitingReviewTaskIsDeterministicallyMovedToReview()
    {
        var decision = BoardStateReconciliationEvaluator.Evaluate(
            Facts("development", "awaiting_review", ["awaiting_review"]));

        Assert.NotNull(decision);
        Assert.Equal("review", decision.TargetState);
    }

    [Fact]
    public void DevelopmentWithoutRunningAttemptCreatesAttention()
    {
        var decision = BoardStateReconciliationEvaluator.Evaluate(
            Facts("development", "ready", []));

        Assert.NotNull(decision);
        Assert.Equal(BoardStateReconciliationEvaluator.Attention, decision.Kind);
        Assert.Equal(
            BoardStateReconciliationEvaluator.DevelopmentWithoutActiveAttempt,
            decision.ReasonCode);
        Assert.Null(decision.TargetState);
    }

    [Fact]
    public void BlockedTaskPreservesItsTransversalState()
    {
        var decision = BoardStateReconciliationEvaluator.Evaluate(
            Facts("blocked", "running", ["running"]));

        Assert.Null(decision);
    }

    [Fact]
    public void ArchivedTaskIsIgnored()
    {
        var decision = BoardStateReconciliationEvaluator.Evaluate(
            Facts("development", "completed", ["approved"], archived: true));

        Assert.Null(decision);
    }

    private static BoardReconciliationFacts Facts(
        string boardState,
        string internalState,
        IReadOnlyList<string> attempts,
        bool archived = false) =>
        new("task-1", boardState, internalState, archived, attempts);
}
