using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class CausalCycleDetectorTests
{
    [Fact]
    public void ClosingEdgeThatCompletesACycleIsDetected()
    {
        var cycle = CausalCycleDetector.DetectClosingCycle(
            [new CausalEdge("a", "b"), new CausalEdge("b", "c")],
            causeKey: "c",
            effectKey: "a");

        Assert.NotNull(cycle);
        Assert.Equal(["c", "a", "b", "c"], cycle!.Path);
    }

    [Fact]
    public void EdgeThatDoesNotCloseAnythingIsAllowed()
    {
        var cycle = CausalCycleDetector.DetectClosingCycle(
            [new CausalEdge("a", "b")],
            causeKey: "b",
            effectKey: "c");

        Assert.Null(cycle);
    }

    [Fact]
    public void SelfGenerationIsTheShortestCycle()
    {
        var cycle = CausalCycleDetector.DetectClosingCycle([], causeKey: "a", effectKey: "a");

        Assert.NotNull(cycle);
        Assert.Equal(["a", "a"], cycle!.Path);
    }

    [Fact]
    public void ShortestPathIsReportedWhenSeveralExist()
    {
        var cycle = CausalCycleDetector.DetectClosingCycle(
            [
                new CausalEdge("a", "b"),
                new CausalEdge("b", "z"),
                new CausalEdge("z", "y"),
                new CausalEdge("y", "c"),
                new CausalEdge("a", "c")
            ],
            causeKey: "c",
            effectKey: "a");

        Assert.NotNull(cycle);
        Assert.Equal(["c", "a", "c"], cycle!.Path);
    }

    [Fact]
    public void ExistingCycleInTheLedgerIsFound()
    {
        var cycle = CausalCycleDetector.DetectExistingCycle(
        [
            new CausalEdge("demand:1", "plan:1"),
            new CausalEdge("plan:1", "card:1"),
            new CausalEdge("card:1", "demand:1")
        ]);

        Assert.NotNull(cycle);
        Assert.Equal("card:1 -> demand:1 -> plan:1 -> card:1", cycle!.Describe());
    }

    [Fact]
    public void AcyclicLedgerReportsNoCycle()
    {
        var cycle = CausalCycleDetector.DetectExistingCycle(
        [
            new CausalEdge("demand:1", "plan:1"),
            new CausalEdge("plan:1", "card:1"),
            new CausalEdge("plan:1", "card:2")
        ]);

        Assert.Null(cycle);
    }

    [Fact]
    public void DuplicatedEdgesDoNotFabricateACycle()
    {
        var cycle = CausalCycleDetector.DetectExistingCycle(
        [
            new CausalEdge("a", "b"),
            new CausalEdge("a", "b"),
            new CausalEdge("a", "b")
        ]);

        Assert.Null(cycle);
    }
}
