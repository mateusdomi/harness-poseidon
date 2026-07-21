using Harness.Modules.Governance.Coordination;

namespace Harness.UnitTests.Governance;

/// <summary>
/// CA-1: o escopo de frontend pertence ao PAPEL, não ao provider. Codex, Kimi Code ou
/// outro executor autorizado exercem o mesmo papel; agentes backend continuam proibidos.
/// </summary>
public sealed class FrontendRoleScopeTests
{
    [Fact]
    public void FrontendRoleKeepsTheSameScopeRegardlessOfProvider()
    {
        // O papel autoriza exatamente frontend/** e docs/frontend/**.
        var allowed = AgentPathScopePolicy.Evaluate(
            AgentPathScopeKind.FrontendSpecialist, ["frontend/**", "docs/frontend/**"]);
        Assert.True(allowed.Allowed);

        // E nada além disso — trocar o executor não amplia escopo.
        var denied = AgentPathScopePolicy.Evaluate(
            AgentPathScopeKind.FrontendSpecialist, ["src/Harness.Host/**"]);
        Assert.False(denied.Allowed);
        Assert.Equal("agent_path_scope_denied", denied.Code);
    }

    [Fact]
    public void BackendAgentsRemainBlockedFromTheFrontendScope()
    {
        var denied = AgentPathScopePolicy.Evaluate(
            AgentPathScopeKind.Backend, ["frontend/src/app.tsx"]);
        Assert.False(denied.Allowed);
        Assert.Contains("frontend/src/app.tsx", denied.RejectedClaims);
    }

    [Fact]
    public void HistoricalKimiAliasResolvesToTheLogicalFrontendRole()
    {
        // Compatibilidade: o alias histórico e o papel são o mesmo escopo.
        Assert.Equal(AgentPathScopeKind.FrontendSpecialist, AgentPathScopeKind.Kimi);
        var viaAlias = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Kimi, ["frontend/**"]);
        var viaRole = AgentPathScopePolicy.Evaluate(
            AgentPathScopeKind.FrontendSpecialist, ["frontend/**"]);
        Assert.Equal(viaRole.Allowed, viaAlias.Allowed);
    }

    [Fact]
    public void EmptyClaimSetIsRejectedForEveryRole()
    {
        Assert.Equal(
            "agent_path_scope_empty",
            AgentPathScopePolicy.Evaluate(AgentPathScopeKind.FrontendSpecialist, []).Code);
    }
}
