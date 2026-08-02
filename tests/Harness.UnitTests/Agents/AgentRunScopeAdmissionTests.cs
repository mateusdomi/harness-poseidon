using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

public sealed class AgentRunScopeAdmissionTests
{
    [Theory]
    [InlineData("docs/**", "docs/decisions/**", true)]
    [InlineData("docs/decisions/**", "docs/decisions/story-map.md", true)]
    [InlineData("docs/architecture/**", "docs/decisions/**", false)]
    [InlineData("src/backend/**", "frontend/**", false)]
    public void ScopePreAdmissionMatchesDurableWorkspaceIntersection(
        string requested,
        string existing,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgentRunOrchestrator.ScopeSetsIntersect([requested], [existing]));
    }

    [Fact]
    public void AnyIntersectingPairDefersTheCandidateBeforeAttemptCreation()
    {
        Assert.True(AgentRunOrchestrator.ScopeSetsIntersect(
            ["docs/architecture/**", "tests/**"],
            ["src/**", "tests/integration/**"]));
        Assert.False(AgentRunOrchestrator.ScopeSetsIntersect(
            ["docs/architecture/**"],
            ["docs/decisions/**", "src/**"]));
    }
}
