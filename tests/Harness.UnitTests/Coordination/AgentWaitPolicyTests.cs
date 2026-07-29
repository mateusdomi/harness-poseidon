using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class AgentWaitPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeadlineExceededTakesCheckpointAndKeepsWaiting()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(30),
            WaitDeadline: Now.AddMinutes(-5),
            LastHeartbeatAt: Now.AddSeconds(-10)));

        Assert.Equal(AgentWaitAction.Checkpoint, verdict.Action);
        Assert.False(verdict.EndsWait);
        Assert.False(verdict.MayReclaim);
        Assert.Equal(AgentWaitPolicy.ReasonDeadlineCheckpoint, verdict.ReasonCode);
    }

    [Fact]
    public void CheckpointIsNotRepeatedForTheSameDeadline()
    {
        var deadline = Now.AddMinutes(-5);
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(30),
            WaitDeadline: deadline,
            LastHeartbeatAt: Now.AddSeconds(-10),
            LastCheckpointAt: deadline.AddSeconds(1)));

        Assert.Equal(AgentWaitAction.KeepWaiting, verdict.Action);
        Assert.False(verdict.EndsWait);
    }

    /// <summary>Gate da fase: um agente vivo jamais é morto pela espera.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    [InlineData(-6000)]
    public void LiveAgentIsNeverReclaimedNoMatterHowLateItIs(int deadlineOffsetMinutes)
    {
        var facts = new AgentWaitFacts(
            Now,
            // Lease vencida há muito tempo — mas o heartbeat é de agora.
            LeaseExpiresAt: Now.AddHours(-3),
            WaitDeadline: Now.AddMinutes(deadlineOffsetMinutes),
            LastHeartbeatAt: Now.AddSeconds(-5),
            ProcessAlive: true);

        var verdict = AgentWaitPolicy.Decide(facts);

        Assert.True(AgentWaitPolicy.IsAgentAlive(facts));
        Assert.False(verdict.MayReclaim);
        Assert.NotEqual(AgentWaitAction.ReclaimProcessDead, verdict.Action);
        Assert.NotEqual(AgentWaitAction.ReclaimLeaseExpiredWithoutHeartbeat, verdict.Action);
    }

    [Fact]
    public void HeartbeatProvesAliveButNeverProvesCompleted()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(30),
            LastHeartbeatAt: Now));

        Assert.Equal(AgentWaitAction.KeepWaiting, verdict.Action);
        Assert.False(verdict.EndsWait);
        Assert.Equal(AgentWaitPolicy.ReasonAgentAlive, verdict.ReasonCode);
    }

    [Fact]
    public void CompletionEndsTheWaitWithoutReclaim()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(30),
            CompletionReported: true));

        Assert.Equal(AgentWaitAction.ConcludeCompleted, verdict.Action);
        Assert.True(verdict.EndsWait);
        Assert.False(verdict.MayReclaim);
    }

    [Fact]
    public void EscalationEndsTheWaitAndIsNotAFailure()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(30),
            LastHeartbeatAt: Now,
            EscalationReported: true));

        Assert.Equal(AgentWaitAction.ConcludeEscalated, verdict.Action);
        Assert.True(verdict.EndsWait);
        Assert.False(verdict.MayReclaim);
    }

    [Fact]
    public void DeadProcessEndsTheWaitAndAllowsReclaim()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(30),
            LastHeartbeatAt: Now.AddSeconds(-1),
            ProcessAlive: false));

        Assert.Equal(AgentWaitAction.ReclaimProcessDead, verdict.Action);
        Assert.True(verdict.EndsWait);
        Assert.True(verdict.MayReclaim);
    }

    [Fact]
    public void ExpiredLeaseWithoutHeartbeatEndsTheWaitAndAllowsReclaim()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(-1),
            LastHeartbeatAt: Now.AddHours(-2)));

        Assert.Equal(AgentWaitAction.ReclaimLeaseExpiredWithoutHeartbeat, verdict.Action);
        Assert.True(verdict.EndsWait);
        Assert.True(verdict.MayReclaim);
    }

    [Fact]
    public void ExpiredLeaseWithRecentHeartbeatKeepsWaiting()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(-1),
            LastHeartbeatAt: Now.AddSeconds(-30)));

        Assert.Equal(AgentWaitAction.KeepWaiting, verdict.Action);
        Assert.False(verdict.MayReclaim);
    }

    [Fact]
    public void MissingHeartbeatAloneDoesNotAuthorizeReclaimWhileLeaseHolds()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(10),
            LastHeartbeatAt: null));

        Assert.Equal(AgentWaitAction.KeepWaiting, verdict.Action);
        Assert.False(verdict.MayReclaim);
    }

    [Fact]
    public void SkewedFutureHeartbeatCountsAsAlive()
    {
        var facts = new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(-5),
            LastHeartbeatAt: Now.AddSeconds(3));

        Assert.True(AgentWaitPolicy.IsAgentAlive(facts));
        Assert.False(AgentWaitPolicy.Decide(facts).MayReclaim);
    }

    [Fact]
    public void CompletionWinsOverDeadProcessBecauseTheReportAlreadyArrived()
    {
        var verdict = AgentWaitPolicy.Decide(new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(30),
            ProcessAlive: false,
            CompletionReported: true));

        Assert.Equal(AgentWaitAction.ConcludeCompleted, verdict.Action);
        Assert.False(verdict.MayReclaim);
    }

    [Fact]
    public void NegativeHeartbeatGraceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentWaitPolicy.Decide(
            new AgentWaitFacts(Now, LeaseExpiresAt: Now.AddMinutes(1)),
            TimeSpan.FromSeconds(-1)));
    }
}
