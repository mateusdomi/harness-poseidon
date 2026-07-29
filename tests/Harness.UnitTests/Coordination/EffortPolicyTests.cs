using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class EffortPolicyTests
{
    /// <summary>Gate da fase: mesma entrada → mesmo orçamento, sempre.</summary>
    [Fact]
    public void TheSameInputAlwaysProducesTheSameBudget()
    {
        foreach (var nature in Enum.GetValues<WorkNature>())
        {
            foreach (var risk in Enum.GetValues<RiskTier>())
            {
                foreach (var small in new[] { true, false })
                {
                    var first = EffortPolicy.Decide(nature, risk, small);
                    for (var repetition = 0; repetition < 5; repetition++)
                    {
                        Assert.Equal(first, EffortPolicy.Decide(nature, risk, small));
                    }
                }
            }
        }
    }

    /// <summary>Demanda visual é um agente, em qualquer risco — dividir o pequeno só coordena.</summary>
    [Theory]
    [InlineData(RiskTier.Low)]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    [InlineData(RiskTier.Critical)]
    public void VisualWorkIsAlwaysASingleAgentWithoutFanOut(RiskTier risk)
    {
        var budget = EffortPolicy.Decide(WorkNature.Visual, risk);

        Assert.Equal(1, budget.Agents);
        Assert.False(budget.FanOutAllowed);
        Assert.Equal(EffortPolicy.ReasonVisualSingleAgent, budget.ReasonCode);
    }

    [Fact]
    public void SmallWorkIsASingleAgentEvenWhenTheAreaIsStructural()
    {
        var budget = EffortPolicy.Decide(WorkNature.Structural, RiskTier.Medium, isSmall: true);

        Assert.Equal(1, budget.Agents);
        Assert.False(budget.FanOutAllowed);
        Assert.Equal(EffortPolicy.ReasonSmallSingleAgent, budget.ReasonCode);
    }

    [Fact]
    public void CriticalStructuralWorkGetsTheDeepestReview()
    {
        var critical = EffortPolicy.Decide(WorkNature.Structural, RiskTier.Critical);
        var ordinary = EffortPolicy.Decide(WorkNature.Implementation, RiskTier.Low);

        Assert.True(critical.ReviewDepth > ordinary.ReviewDepth);
        Assert.True(critical.TokenBudget > ordinary.TokenBudget);
        Assert.True(critical.MaxRounds >= ordinary.MaxRounds);
    }

    [Fact]
    public void RiskNeverLowersTheBudget()
    {
        foreach (var nature in new[] { WorkNature.Implementation, WorkNature.Structural })
        {
            var tiers = Enum.GetValues<RiskTier>()
                .Select(risk => EffortPolicy.Decide(nature, risk))
                .ToArray();

            for (var index = 1; index < tiers.Length; index++)
            {
                Assert.True(tiers[index].TokenBudget >= tiers[index - 1].TokenBudget);
                Assert.True(tiers[index].ReviewDepth >= tiers[index - 1].ReviewDepth);
            }
        }
    }

    [Fact]
    public void InvestigationIsOneAgentBecauseMoreProduceDivergentReports()
    {
        var budget = EffortPolicy.Decide(WorkNature.Investigation, RiskTier.High);

        Assert.Equal(1, budget.Agents);
        Assert.False(budget.FanOutAllowed);
        Assert.Equal(0, budget.ReviewDepth);
    }

    /// <summary>Gate da fase: só direções independentes E com valor viram fan-out.</summary>
    [Fact]
    public void DirectionsSharingScopeAreNotParallelTheyAreAScheduledConflict()
    {
        var budget = EffortPolicy.Decide(WorkNature.Structural, RiskTier.Critical);
        var selected = EffortPolicy.SelectFanOut(
        [
            new FanOutDirection("backend", ["src/Modules/A"], CarriesValue: true),
            new FanOutDirection("tambem-backend", ["src/Modules/A"], CarriesValue: true)
        ], budget);

        // Duas direções sobre o mesmo escopo: sobra uma, e uma direção não é fan-out.
        Assert.Empty(selected);
    }

    [Fact]
    public void IndependentValuableDirectionsAreParallelized()
    {
        var budget = EffortPolicy.Decide(WorkNature.Structural, RiskTier.Critical);
        var selected = EffortPolicy.SelectFanOut(
        [
            new FanOutDirection("backend", ["src/Modules/A"], CarriesValue: true),
            new FanOutDirection("frontend", ["frontend/src/features/x"], CarriesValue: true)
        ], budget);

        Assert.Equal(["backend", "frontend"], selected.Select(direction => direction.Key));
    }

    [Fact]
    public void DirectionsWithoutValueAreNotParallelizedJustBecauseTheyFit()
    {
        var budget = EffortPolicy.Decide(WorkNature.Structural, RiskTier.Critical);
        var selected = EffortPolicy.SelectFanOut(
        [
            new FanOutDirection("backend", ["src/Modules/A"], CarriesValue: true),
            new FanOutDirection("cosmetico", ["docs/x"], CarriesValue: false)
        ], budget);

        Assert.Empty(selected);
    }

    [Fact]
    public void FanOutNeverExceedsTheAgentBudget()
    {
        var budget = EffortPolicy.Decide(WorkNature.Structural, RiskTier.Critical);
        var selected = EffortPolicy.SelectFanOut(
        [
            new FanOutDirection("a", ["s/a"], true),
            new FanOutDirection("b", ["s/b"], true),
            new FanOutDirection("c", ["s/c"], true),
            new FanOutDirection("d", ["s/d"], true)
        ], budget);

        Assert.Equal(budget.Agents, selected.Count);
    }

    [Fact]
    public void SingleAgentBudgetsNeverFanOut()
    {
        var budget = EffortPolicy.Decide(WorkNature.Visual, RiskTier.Low);
        Assert.Empty(EffortPolicy.SelectFanOut(
            [new FanOutDirection("a", ["s/a"], true), new FanOutDirection("b", ["s/b"], true)],
            budget));
    }

    /// <summary>Assimetria: revisão de risco alto vai no modelo forte, execução comum no barato.</summary>
    [Fact]
    public void ReviewOfHighRiskGoesToTheStrongestModelAndOrdinaryWorkToTheCheapest()
    {
        Assert.Equal(ModelTier.Strong, EffortPolicy.RouteModel(RiskTier.High, isReview: true));
        Assert.Equal(ModelTier.Strong, EffortPolicy.RouteModel(RiskTier.Critical, isReview: true));
        Assert.Equal(ModelTier.Balanced, EffortPolicy.RouteModel(RiskTier.Low, isReview: true));
        Assert.Equal(ModelTier.Economical, EffortPolicy.RouteModel(RiskTier.Low, isReview: false));
        Assert.Equal(ModelTier.Economical, EffortPolicy.RouteModel(RiskTier.High, isReview: false));
    }

    [Fact]
    public void UndefinedInputsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EffortPolicy.Decide((WorkNature)99, RiskTier.Low));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EffortPolicy.Decide(WorkNature.Visual, (RiskTier)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EffortPolicy.RouteModel((RiskTier)99, isReview: true));
    }
}
