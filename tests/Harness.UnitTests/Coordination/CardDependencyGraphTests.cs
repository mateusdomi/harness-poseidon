using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class CardDependencyGraphTests
{
    [Fact]
    public void BuildsDeterministicFanOutAndFanInWaves()
    {
        var plan = CardDependencyGraph.Build(
        [
            Node("architecture", ["contract"], []),
            Node("backend", ["backend-api"], ["contract"]),
            Node("frontend", ["frontend-app"], ["contract"]),
            Node("integration", ["release-candidate"], ["backend-api", "frontend-app"]),
        ]);

        Assert.True(plan.IsValid);
        Assert.Equal(
        [
            ["architecture"],
            ["backend", "frontend"],
            ["integration"],
        ], plan.DispatchWaves);
        var barrier = Assert.Single(plan.FanInBarriers);
        Assert.Equal("integration", barrier.ConsumerCardId);
        Assert.Equal(["backend", "frontend"], barrier.ProviderCardIds);
    }

    [Fact]
    public void ReleasesOnlyCardsWhoseProvidersAreCompleted()
    {
        var plan = CardDependencyGraph.Build(
        [
            Node("architecture", ["contract"], []),
            Node("backend", ["backend-api"], ["contract"]),
            Node("frontend", ["frontend-app"], ["contract"]),
            Node("integration", ["release-candidate"], ["backend-api", "frontend-app"]),
        ]);

        Assert.Equal(["architecture"], plan.GetReadyCards(new HashSet<string>()));
        Assert.Equal(
            ["backend", "frontend"],
            plan.GetReadyCards(new HashSet<string> { "architecture" }));
        Assert.Empty(plan.GetReadyCards(new HashSet<string> { "architecture", "backend" }));
        Assert.Equal(
            ["integration"],
            plan.GetReadyCards(new HashSet<string> { "architecture", "backend", "frontend" }));
    }

    [Fact]
    public void FailsClosedWhenAConsumedResourceHasNoProvider()
    {
        var plan = CardDependencyGraph.Build(
        [
            Node("frontend", ["frontend-app"], ["missing-contract"]),
        ]);

        Assert.False(plan.IsValid);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal("missing_resource_provider", issue.Code);
        Assert.Equal("frontend", issue.CardId);
        Assert.Equal("missing-contract", issue.Resource);
        Assert.Empty(plan.GetReadyCards(new HashSet<string>()));
    }

    [Fact]
    public void AcceptsExplicitExternalInputsAsRootResources()
    {
        var plan = CardDependencyGraph.Build(
        [
            Node("discovery", ["prd"], ["user-request"]),
        ], ["user-request"]);

        Assert.True(plan.IsValid);
        Assert.Equal(["discovery"], Assert.Single(plan.DispatchWaves));
    }

    [Fact]
    public void FailsClosedForAmbiguousProviders()
    {
        var plan = CardDependencyGraph.Build(
        [
            Node("backend-a", ["api"], []),
            Node("backend-b", ["API"], []),
            Node("frontend", ["ui"], ["api"]),
        ]);

        Assert.False(plan.IsValid);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal("ambiguous_resource_provider", issue.Code);
        Assert.Equal(["backend-a", "backend-b"], issue.RelatedCardIds);
    }

    [Fact]
    public void DetectsCyclesAndSuppressesDispatch()
    {
        var plan = CardDependencyGraph.Build(
        [
            Node("a", ["resource-a"], ["resource-b"]),
            Node("b", ["resource-b"], ["resource-a"]),
        ]);

        Assert.False(plan.IsValid);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal("dependency_cycle", issue.Code);
        Assert.Equal(["a", "b"], issue.RelatedCardIds);
        Assert.Empty(plan.DispatchWaves);
        Assert.Empty(plan.GetReadyCards(new HashSet<string>()));
    }

    [Fact]
    public void RejectsDuplicateCardsAndInvalidResourceReferences()
    {
        Assert.Throws<ArgumentException>(() => CardDependencyGraph.Build(
        [
            Node("same", ["a"], []),
            Node("same", ["b"], []),
        ]));
        Assert.Throws<ArgumentException>(() => CardDependencyGraph.Build(
        [
            Node("card", ["\u0000"], []),
        ]));
    }

    private static CardDependencyNode Node(
        string cardId,
        IReadOnlyList<string> provides,
        IReadOnlyList<string> consumes) =>
        new(cardId, provides, consumes);
}
