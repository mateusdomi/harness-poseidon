using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class PlanDepthPolicyTests
{
    [Fact]
    public void PlanWithinTheLimitKeepsOneLevelPerWave()
    {
        var assessment = PlanDepthPolicy.Evaluate([["a"], ["b"], ["c"]]);

        Assert.Equal(3, assessment.Depth);
        Assert.False(assessment.ExceedsLimit);
        Assert.Equal(3, assessment.Groups.Count);
        Assert.All(assessment.Groups, group => Assert.False(group.IsRegrouped));
    }

    [Fact]
    public void DeepPlanIsRegroupedIntoAtMostMaxDepthWaves()
    {
        var waves = new IReadOnlyList<string>[]
        {
            ["a"], ["b"], ["c"], ["d"], ["e"], ["f"], ["g"]
        };

        var assessment = PlanDepthPolicy.Evaluate(waves, maxDepth: 4);

        Assert.Equal(7, assessment.Depth);
        Assert.True(assessment.ExceedsLimit);
        Assert.Equal(4, assessment.Groups.Count);
        Assert.Equal(["a", "b"], assessment.Groups[0].CardIds);
        Assert.Equal(["c", "d"], assessment.Groups[1].CardIds);
        Assert.Equal(["e", "f"], assessment.Groups[2].CardIds);
        Assert.Equal(["g"], assessment.Groups[3].CardIds);
    }

    [Fact]
    public void RegroupingPreservesEveryCardAndTheOrderBetweenLevels()
    {
        var waves = new IReadOnlyList<string>[]
        {
            ["a1", "a2"], ["b"], ["c"], ["d"], ["e"], ["f"]
        };

        var assessment = PlanDepthPolicy.Evaluate(waves, maxDepth: 3);

        var flattened = assessment.Groups.SelectMany(group => group.CardIds).ToArray();
        Assert.Equal(["a1", "a2", "b", "c", "d", "e", "f"], flattened);
        Assert.Equal(3, assessment.Groups.Count);
        Assert.Contains(assessment.Groups, group => group.IsRegrouped);
    }

    [Fact]
    public void DispatchDoesNotWaitForAWaveBecauseReadinessIsPerCard()
    {
        // a -> b -> c -> d -> e (cadeia funda de propósito)
        var plan = CardDependencyGraph.Build(
        [
            new CardDependencyNode("a", ["ra"], []),
            new CardDependencyNode("b", ["rb"], ["ra"]),
            new CardDependencyNode("c", ["rc"], ["rb"]),
            new CardDependencyNode("d", ["rd"], ["rc"]),
            new CardDependencyNode("e", [], ["rd"])
        ]);

        var assessment = PlanDepthPolicy.Evaluate(plan, maxDepth: 3);
        Assert.Equal(5, assessment.Depth);
        Assert.True(assessment.ExceedsLimit);
        Assert.Equal(3, assessment.Groups.Count);

        // Reagrupar não antecipou despacho: 'c' só fica pronto quando 'b' conclui, mesmo estando
        // na mesma onda que 'd'.
        Assert.Equal(["a"], plan.GetReadyCards(new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(
            ["b"],
            plan.GetReadyCards(new HashSet<string>(["a"], StringComparer.Ordinal)));
        Assert.Equal(
            ["c"],
            plan.GetReadyCards(new HashSet<string>(["a", "b"], StringComparer.Ordinal)));
    }

    [Fact]
    public void MaxDepthBelowTheHomologatedFloorIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanDepthPolicy.Evaluate([["a"]], maxDepth: 2));
    }

    [Fact]
    public void EmptyPlanHasNoDepth()
    {
        var assessment = PlanDepthPolicy.Evaluate([]);

        Assert.Equal(0, assessment.Depth);
        Assert.False(assessment.ExceedsLimit);
        Assert.Empty(assessment.Groups);
    }
}
