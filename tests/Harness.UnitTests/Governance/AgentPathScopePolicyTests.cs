using Harness.Modules.Governance.Coordination;

namespace Harness.UnitTests.Governance;

public sealed class AgentPathScopePolicyTests
{
    [Theory]
    [InlineData("frontend/**")]
    [InlineData("frontend/src/App.tsx")]
    [InlineData("docs/frontend/**")]
    public void KimiAcceptsOnlyItsOwnedRoots(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Kimi, [claim]);

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("src/**")]
    // `governance/**` SAIU daqui: a varredura ampla da raiz de governança engole as duas fontes
    // canônicas, e elas são a regra que restringe o próprio agente. Um claim estreito dentro de
    // governança continua aceito — ver `BackendKeepsNarrowGovernanceClaims`.
    [InlineData("governance/rules/**")]
    [InlineData("CLAUDE.md")]
    [InlineData("docs/backend/security/THREAT_MODEL.md")]
    public void BackendAcceptsOwnedOrExplicitlySharedClaims(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Backend, [claim]);

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("src/**")]
    [InlineData("governance/**")]
    [InlineData("../frontend/**")]
    [InlineData("/**")]
    public void KimiRejectsNonFrontendOrUnsafeClaims(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Kimi, [claim]);

        Assert.False(result.Allowed);
        Assert.Equal("agent_path_scope_denied", result.Code);
    }

    [Theory]
    [InlineData("frontend/**")]
    [InlineData("docs/frontend/**")]
    [InlineData("docs/**")]
    [InlineData("**")]
    // Imutabilidade de guardrail: o canon e a varredura que o engloba são negados para TODO papel.
    [InlineData("governance/**")]
    [InlineData("governance/core.md")]
    [InlineData("governance/manifest.yaml")]
    public void BackendRejectsFrontendAndOverbroadClaims(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Backend, [claim]);

        Assert.False(result.Allowed);
    }
}
