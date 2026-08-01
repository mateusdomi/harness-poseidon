using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class CardReadinessEvaluatorTests
{
    [Theory]
    [InlineData("agent_task")]
    [InlineData("spike")]
    [InlineData("historia")]
    [InlineData("tarefa")]
    [InlineData("bug")]
    [InlineData("adr")]
    [InlineData("documento")]
    [InlineData("revisao")]
    [InlineData("incidente")]
    [InlineData("chamado")]
    public void WorkCardWithInstructionAndNotBlockedIsDispatchable(string cardType)
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(cardType, HasInstruction: true, IsBlocked: false));

        Assert.True(snapshot.IsDispatchable);
        Assert.Empty(snapshot.Blockers);
    }

    [Theory]
    [InlineData("human_gate")]
    [InlineData("decision")]
    [InlineData("feature")]
    [InlineData("gate")]
    public void ContainerAndDecisionCardTypesAreNeverDispatchable(string cardType)
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts(cardType, HasInstruction: true, IsBlocked: false));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains(CardReadinessEvaluator.CardTypeNotDispatchable, snapshot.Blockers);
    }

    [Fact]
    public void MissingInstructionBlocksDispatch()
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts("agent_task", HasInstruction: false, IsBlocked: false));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains(CardReadinessEvaluator.InstructionMissing, snapshot.Blockers);
    }

    [Fact]
    public void BlockedCardIsNotDispatchable()
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts("agent_task", HasInstruction: true, IsBlocked: true));

        Assert.False(snapshot.IsDispatchable);
        Assert.Contains(CardReadinessEvaluator.Blocked, snapshot.Blockers);
    }

    [Fact]
    public void EveryFailingFactContributesItsOwnTypedBlocker()
    {
        var snapshot = CardReadinessEvaluator.Evaluate(
            new CardReadinessFacts("human_gate", HasInstruction: false, IsBlocked: true));

        Assert.False(snapshot.IsDispatchable);
        Assert.Equal(
            new[]
            {
                CardReadinessEvaluator.CardTypeNotDispatchable,
                CardReadinessEvaluator.InstructionMissing,
                CardReadinessEvaluator.Blocked,
            },
            snapshot.Blockers);
    }
}
