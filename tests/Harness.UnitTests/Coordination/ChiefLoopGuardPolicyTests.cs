using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class ChiefLoopGuardPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HumanTriggeredTurnIsNeverBlocked()
    {
        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: false, "plan-1"),
            new ChiefLoopGuardFacts(
                SelfTriggeredTurnsForDemand: 9_999,
                PlanTurnInFlight: true,
                RecentSelfTriggeredTurns: [Now, Now, Now, Now, Now]));

        Assert.True(verdict.Allowed);
        Assert.Equal(ChiefLoopGuardPolicy.ReasonHumanTriggered, verdict.ReasonCode);
    }

    [Fact]
    public void OrdinarySelfTriggeredTurnIsAllowed()
    {
        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true, "plan-1"),
            new ChiefLoopGuardFacts(SelfTriggeredTurnsForDemand: 1));

        Assert.True(verdict.Allowed);
        Assert.Equal(ChiefLoopGuardPolicy.ReasonAllowed, verdict.ReasonCode);
    }

    [Fact]
    public void DemandCeilingBlocksTheSelfTriggeredTurn()
    {
        var limits = new ChiefLoopGuardLimits(MaxSelfTriggeredTurnsPerDemand: 3);
        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true),
            new ChiefLoopGuardFacts(SelfTriggeredTurnsForDemand: 3),
            limits);

        Assert.False(verdict.Allowed);
        Assert.Equal(ChiefLoopGuardPolicy.ReasonDemandCeiling, verdict.ReasonCode);
    }

    [Fact]
    public void PlanWithTurnInFlightDoesNotReenter()
    {
        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true, "plan-1"),
            new ChiefLoopGuardFacts(PlanTurnInFlight: true));

        Assert.False(verdict.Allowed);
        Assert.Equal(ChiefLoopGuardPolicy.ReasonPlanReentrancy, verdict.ReasonCode);
        Assert.Contains("plan-1", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReentrancyGuardNeedsAPlanToApply()
    {
        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true),
            new ChiefLoopGuardFacts(PlanTurnInFlight: true));

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void RateWindowBlocksABurstButNotASpreadOutHistory()
    {
        var limits = new ChiefLoopGuardLimits(
            MaxSelfTriggeredTurnsPerDemand: 100,
            MaxSelfTriggeredTurnsPerWindow: 3,
            RateWindow: TimeSpan.FromMinutes(10));

        var burst = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true),
            new ChiefLoopGuardFacts(RecentSelfTriggeredTurns:
            [
                Now.AddMinutes(-1), Now.AddMinutes(-2), Now.AddMinutes(-3)
            ]),
            limits);

        Assert.False(burst.Allowed);
        Assert.Equal(ChiefLoopGuardPolicy.ReasonRateWindow, burst.ReasonCode);

        var spread = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true),
            new ChiefLoopGuardFacts(RecentSelfTriggeredTurns:
            [
                Now.AddMinutes(-11), Now.AddHours(-2), Now.AddHours(-5)
            ]),
            limits);

        Assert.True(spread.Allowed);
    }

    /// <summary>Gate da fase: A gerou B que regenerou A é detectado e interrompido com evidência.</summary>
    [Fact]
    public void CausalCycleIsDetectedAndInterruptedWithAuditableEvidence()
    {
        // A demanda gerou o plano, o plano gerou o card, e agora o card quer regenerar a demanda.
        var demand = ChiefLoopGuardPolicy.DemandKey("demand-1");
        var plan = ChiefLoopGuardPolicy.PlanKey("plan-1");
        var card = ChiefLoopGuardPolicy.CardKey("card-1");

        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true, "plan-1", CauseKey: card),
            new ChiefLoopGuardFacts(CausalEdges:
            [
                new CausalEdge(demand, plan),
                new CausalEdge(plan, card)
            ]));

        Assert.False(verdict.Allowed);
        Assert.Equal(ChiefLoopGuardPolicy.ReasonCausalCycle, verdict.ReasonCode);
        Assert.NotNull(verdict.Cycle);
        Assert.Equal([card, demand, plan, card], verdict.Cycle!.Path);
        Assert.Equal(
            "card:card-1 -> demand:demand-1 -> plan:plan-1 -> card:card-1",
            verdict.Cycle.Describe());
    }

    [Fact]
    public void CausalCycleIsCheckedBeforeTheSymptomGuards()
    {
        var demand = ChiefLoopGuardPolicy.DemandKey("demand-1");
        var card = ChiefLoopGuardPolicy.CardKey("card-1");

        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true, "plan-1", CauseKey: card),
            new ChiefLoopGuardFacts(
                SelfTriggeredTurnsForDemand: 500,
                PlanTurnInFlight: true,
                CausalEdges: [new CausalEdge(demand, card)]));

        // A causa vence o sintoma: o motivo relatado é o ciclo, não o teto nem a reentrância.
        Assert.Equal(ChiefLoopGuardPolicy.ReasonCausalCycle, verdict.ReasonCode);
    }

    [Fact]
    public void UnrelatedCausalHistoryDoesNotBlock()
    {
        var verdict = ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger(
                "demand-1", Now, SelfTriggered: true,
                CauseKey: ChiefLoopGuardPolicy.CardKey("card-9")),
            new ChiefLoopGuardFacts(CausalEdges:
            [
                new CausalEdge(
                    ChiefLoopGuardPolicy.DemandKey("demand-2"),
                    ChiefLoopGuardPolicy.PlanKey("plan-2"))
            ]));

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void InvalidLimitsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChiefLoopGuardPolicy.Evaluate(
            new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true),
            new ChiefLoopGuardFacts(),
            new ChiefLoopGuardLimits(MaxSelfTriggeredTurnsPerDemand: 0)));
    }
}
