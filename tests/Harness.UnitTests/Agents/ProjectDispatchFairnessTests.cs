using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.UnitTests.Agents;

public sealed class ProjectDispatchFairnessTests
{
    [Fact]
    public void NextCycleStartsAfterTheLastProjectThatOccupiedASlot()
    {
        var projects = new[] { Project("A"), Project("B"), Project("C"), Project("D") };

        var rotated = ChiefBacklogLoopService.RotateProjectsAfter(projects, "B");

        Assert.Equal(["C", "D", "A", "B"], rotated.Select(project => project.Id));
    }

    [Fact]
    public void LastProjectWrapsToTheCanonicalBeginning()
    {
        var projects = new[] { Project("A"), Project("B"), Project("C") };

        var rotated = ChiefBacklogLoopService.RotateProjectsAfter(projects, "C");

        Assert.Equal(["A", "B", "C"], rotated.Select(project => project.Id));
        Assert.NotSame(projects, rotated);
    }

    [Fact]
    public void MissingCursorPreservesTheCanonicalOrder()
    {
        var projects = new[] { Project("A"), Project("B"), Project("C") };

        var rotated = ChiefBacklogLoopService.RotateProjectsAfter(projects, "REMOVED");

        Assert.Same(projects, rotated);
    }

    private static ProjectRecord Project(string id) =>
        new(
            "tenant", id, "organization", $"Project {id}", id, "description", "active",
            "medium", "/tmp/repository", "local", "develop", [],
            new ProjectBrandRecord(null, null, null, null), [], 1, $"chief-{id}", "autonomous",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
}
