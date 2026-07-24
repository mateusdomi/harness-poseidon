using Harness.Host.Agents;
using Harness.Host.Architecture;

namespace Harness.UnitTests.Architecture;

/// <summary>
/// UX-PROTO/ARCH: o gate do seed do mapa de arquitetura é PURO e testável sem host — só semeia quando
/// a execução de agentes está habilitada (<c>Harness:AgentRuns:Enabled</c>), o mesmo padrão dos
/// demais seeders de startup.
/// </summary>
public sealed class ArchitectureSelfMapSeedGateTests
{
    [Fact]
    public void RunsWhenAgentRunsEnabled() =>
        Assert.True(ArchitectureSelfMapSeedHostedService.ShouldRun(new AgentRunSettings { Enabled = true }));

    [Fact]
    public void DoesNotRunWhenAgentRunsDisabled() =>
        Assert.False(ArchitectureSelfMapSeedHostedService.ShouldRun(new AgentRunSettings { Enabled = false }));

    [Fact]
    public void DoesNotRunWhenSettingsMissing() =>
        Assert.False(ArchitectureSelfMapSeedHostedService.ShouldRun(null));
}
