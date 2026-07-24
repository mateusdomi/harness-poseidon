using Harness.Host.Agents;
using Harness.Host.Projects;

namespace Harness.UnitTests.Projects;

/// <summary>
/// RN-03: o gate PURO que decide se a convergência de estado do projeto roda no startup. Igual à
/// convergência de workflow, NÃO exige conta do Chefe — basta a execução de agentes estar habilitada.
/// </summary>
public sealed class ProjectStateConvergenceGateTests
{
    [Fact]
    public void DoesNotRunWhenAgentRunsAreDisabled() =>
        Assert.False(ProjectStateConvergenceHostedService.ShouldRun(new AgentRunSettings { Enabled = false }));

    [Fact]
    public void DoesNotRunWhenSettingsAreMissing() =>
        Assert.False(ProjectStateConvergenceHostedService.ShouldRun(null));

    [Fact]
    public void RunsWhenAgentRunsAreEnabled() =>
        Assert.True(ProjectStateConvergenceHostedService.ShouldRun(new AgentRunSettings { Enabled = true }));
}
