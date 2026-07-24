using Harness.Host.Agents;
using Harness.Host.Providers;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;

namespace Harness.UnitTests.Providers;

/// <summary>
/// GP-06 (fecho): o gate PURO que decide se o seed do catálogo CLI do Chefe roda. Só semeia
/// quando a execução de agentes está ligada E existe no registro uma conta do papel
/// <c>chief-orchestrator</c> não desabilitada — exatamente o critério que o executor de conversa
/// usa para resolver a conta do Chefe. Sem cota, sem probe: presunção seria desonesta.
/// </summary>
public sealed class ChiefCliProviderCatalogSeedGateTests
{
    [Fact]
    public void DoesNotSeedWhenAgentRunsAreDisabled()
    {
        var settings = new AgentRunSettings { Enabled = false };

        Assert.False(ChiefCliProviderCatalogSeedHostedService.ShouldSeed(settings, ChiefRegistry()));
    }

    [Fact]
    public void DoesNotSeedWhenNoChiefAccountIsPresent()
    {
        var settings = new AgentRunSettings { Enabled = true };

        Assert.False(ChiefCliProviderCatalogSeedHostedService.ShouldSeed(settings, new AgentAccountRegistry()));
    }

    [Fact]
    public void DoesNotSeedWhenTheOnlyChiefAccountIsDisabled()
    {
        var settings = new AgentRunSettings { Enabled = true };
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.Disabled));

        Assert.False(ChiefCliProviderCatalogSeedHostedService.ShouldSeed(settings, registry));
    }

    [Fact]
    public void SeedsWhenEnabledAndAnEnabledChiefAccountIsPresent()
    {
        var settings = new AgentRunSettings { Enabled = true };

        Assert.True(ChiefCliProviderCatalogSeedHostedService.ShouldSeed(settings, ChiefRegistry()));
    }

    [Fact]
    public void DoesNotSeedWhenAnEnabledAccountLacksTheChiefRole()
    {
        var settings = new AgentRunSettings { Enabled = true };
        var registry = new AgentAccountRegistry();
        registry.Register(new AgentAccountContract(
            "worker-claude-secondary", "anthropic", ExecutorCatalog.ClaudeCode,
            "keychain://poseidon/worker-claude-secondary", "confighome://worker-claude-secondary",
            [AgentRoles.BackendSpecialist], [],
            AgentAccountState.Available, AgentAccountHealth.Unknown, 1, 0, null, null, null, null, null, 100));

        Assert.False(ChiefCliProviderCatalogSeedHostedService.ShouldSeed(settings, registry));
    }

    private static AgentAccountRegistry ChiefRegistry()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.AuthenticationRequired));
        return registry;
    }

    private static AgentAccountContract ChiefAccount(AgentAccountState state) =>
        new(
            "chief-claude-primary", "anthropic", ExecutorCatalog.ClaudeCode,
            "keychain://poseidon/chief-claude-primary", "confighome://chief-claude-primary",
            [AgentRoles.ChiefOrchestrator], [],
            state, AgentAccountHealth.Unknown, 1, 0, null, null, null, null, null, 100);
}
