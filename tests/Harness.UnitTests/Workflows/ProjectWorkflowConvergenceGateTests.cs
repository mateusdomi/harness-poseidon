using Harness.Host.Agents;
using Harness.Host.Workflows;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// RN-02: o gate PURO que decide se a convergência de workflow roda no startup. Diferente do gate
/// do fecho GP-06, NÃO exige uma conta do Chefe: a invariante "todo projeto tem workflow" independe
/// de haver uma conta de execução configurada — basta a execução de agentes estar habilitada.
/// </summary>
public sealed class ProjectWorkflowConvergenceGateTests
{
    [Fact]
    public void DoesNotRunWhenAgentRunsAreDisabled() =>
        Assert.False(ProjectWorkflowConvergenceHostedService.ShouldRun(new AgentRunSettings { Enabled = false }));

    [Fact]
    public void DoesNotRunWhenSettingsAreMissing() =>
        Assert.False(ProjectWorkflowConvergenceHostedService.ShouldRun(null));

    [Fact]
    public void RunsWhenAgentRunsAreEnabled() =>
        Assert.True(ProjectWorkflowConvergenceHostedService.ShouldRun(new AgentRunSettings { Enabled = true }));
}
