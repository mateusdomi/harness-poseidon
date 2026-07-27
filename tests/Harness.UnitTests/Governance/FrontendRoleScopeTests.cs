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

    [Fact]
    public void NoAgentRoleCanClaimTheCanonicalGovernanceSources()
    {
        // Imutabilidade de guardrail: as duas fontes canônicas SÃO a regra que restringe o agente.
        // Deixar o restringido reescrever a própria restrição é confused deputy — nenhuma outra
        // trava do sistema sobrevive a isso. Antes desta guarda, `governance/**` entrava pela raiz
        // compartilhada e ia junto no escopo padrão de qualquer card de backend.
        foreach (var kind in new[] { AgentPathScopeKind.Backend, AgentPathScopeKind.FrontendSpecialist })
        {
            Assert.False(AgentPathScopePolicy.Evaluate(kind, ["governance/**"]).Allowed);
            Assert.False(AgentPathScopePolicy.Evaluate(kind, ["governance/core.md"]).Allowed);
            Assert.False(AgentPathScopePolicy.Evaluate(kind, ["governance/manifest.yaml"]).Allowed);
        }
    }

    [Fact]
    public void ANarrowGovernanceClaimRemainsPossibleForBackend()
    {
        // A negação é do canon e da varredura ampla, não da governança inteira: um card que
        // produz um documento de governança declara um claim estreito e continua valendo.
        Assert.True(AgentPathScopePolicy.Evaluate(
            AgentPathScopeKind.Backend, ["governance/rules/**"]).Allowed);
    }

    [Fact]
    public void TheBackendDefaultScopeDoesNotSweepGovernance()
    {
        // Trava de regressão do par: se a varredura voltar ao escopo padrão, todo card de backend
        // volta a carregar poder sobre o canon — e o dispatch inteiro passa a ser rejeitado.
        Assert.DoesNotContain(
            Harness.Modules.Agents.Application.Accounts.AgentRoles.PathScopesFor("backend-specialist"),
            scope => scope.StartsWith("governance", StringComparison.Ordinal));
    }
}
