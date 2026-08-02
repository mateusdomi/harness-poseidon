using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Projects;

namespace Harness.UnitTests.Agents;

public sealed class ProjectDispatchFairnessTests
{
    [Fact]
    public void ProjectsThatNeverOccupiedASlotPrecedeAlreadyServedProjects()
    {
        var projects = new[] { Project("A", 30), Project("B", 10), Project("C", 20) };
        var counts = new Dictionary<string, int> { ["A"] = 2, ["B"] = 0, ["C"] = 1 };

        var ordered = ChiefBacklogLoopService.OrderProjectsForDispatch(projects, counts);

        Assert.Equal(["B", "C", "A"], ordered.Select(project => project.Id));
    }

    [Fact]
    public void RecentBusinessActivityBreaksAnEqualServiceTie()
    {
        var projects = new[] { Project("A", 10), Project("B", 30), Project("C", 20) };

        var ordered = ChiefBacklogLoopService.OrderProjectsForDispatch(
            projects, new Dictionary<string, int>());

        Assert.Equal(["B", "C", "A"], ordered.Select(project => project.Id));
    }

    [Fact]
    public void ProjectIdClosesAnExactTieDeterministically()
    {
        var projects = new[] { Project("C", 10), Project("A", 10), Project("B", 10) };

        var ordered = ChiefBacklogLoopService.OrderProjectsForDispatch(
            projects, new Dictionary<string, int>());

        Assert.Equal(["A", "B", "C"], ordered.Select(project => project.Id));
    }

    private static ProjectRecord Project(string id, int activityMinutes) =>
        new(
            "tenant", id, "organization", $"Project {id}", id, "description", "active",
            "medium", "/tmp/repository", "local", "develop", [],
            new ProjectBrandRecord(null, null, null, null), [], 1, $"chief-{id}", "autonomous",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(activityMinutes), 1);
}
