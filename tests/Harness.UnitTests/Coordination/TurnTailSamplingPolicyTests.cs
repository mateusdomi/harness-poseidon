using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class TurnTailSamplingPolicyTests
{
    [Fact]
    public void GuardBlockedTurnIsAlwaysKept()
    {
        var decision = TurnTailSamplingPolicy.Decide(new TurnTailSamplingFacts(
            "trace-1", TimeSpan.FromMilliseconds(5), "completed", GuardBlocked: true));

        Assert.True(decision.Keep);
        Assert.Equal(TurnTailSamplingPolicy.ReasonGuardBlocked, decision.ReasonCode);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("escalated")]
    public void EveryOutcomeOtherThanCompletedIsTail(string outcome)
    {
        var decision = TurnTailSamplingPolicy.Decide(
            new TurnTailSamplingFacts("trace-1", TimeSpan.FromMilliseconds(5), outcome));

        Assert.True(decision.Keep);
        Assert.Equal(TurnTailSamplingPolicy.ReasonNotCompleted, decision.ReasonCode);
    }

    [Fact]
    public void SlowTurnIsKeptEvenWhenItCompleted()
    {
        var decision = TurnTailSamplingPolicy.Decide(
            new TurnTailSamplingFacts("trace-1", TimeSpan.FromMinutes(4), "completed"),
            new TurnTailSamplingOptions(SlowThreshold: TimeSpan.FromSeconds(30)));

        Assert.True(decision.Keep);
        Assert.Equal(TurnTailSamplingPolicy.ReasonSlow, decision.ReasonCode);
    }

    [Fact]
    public void FastCompletedTurnsAreSampledDownButNotErased()
    {
        var options = new TurnTailSamplingOptions(BaselineKeepEvery: 10);
        var kept = Enumerable.Range(0, 1_000)
            .Count(index => TurnTailSamplingPolicy.Decide(
                new TurnTailSamplingFacts(
                    $"trace-{index}", TimeSpan.FromMilliseconds(20), "completed"),
                options).Keep);

        // Fração aproximada de 1/10 — o corpo da distribuição continua representado.
        Assert.InRange(kept, 50, 160);
    }

    [Fact]
    public void TheSameTraceAlwaysDecidesTheSameWay()
    {
        var facts = new TurnTailSamplingFacts(
            "trace-estavel", TimeSpan.FromMilliseconds(20), "completed");

        var first = TurnTailSamplingPolicy.Decide(facts);
        for (var repetition = 0; repetition < 25; repetition++)
        {
            Assert.Equal(first.Keep, TurnTailSamplingPolicy.Decide(facts).Keep);
        }
    }

    [Fact]
    public void KeepingEveryTurnIsPossibleByConfiguration()
    {
        var options = new TurnTailSamplingOptions(BaselineKeepEvery: 1);
        Assert.All(
            Enumerable.Range(0, 50),
            index => Assert.True(TurnTailSamplingPolicy.Decide(
                new TurnTailSamplingFacts(
                    $"trace-{index}", TimeSpan.FromMilliseconds(1), "completed"),
                options).Keep));
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnTailSamplingPolicy.Decide(
            new TurnTailSamplingFacts("trace-1", TimeSpan.Zero, "completed"),
            new TurnTailSamplingOptions(BaselineKeepEvery: 0)));
    }
}
