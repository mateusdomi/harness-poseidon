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
    // Fuga de escopo: subir de diretório, caminho absoluto e `..` embutido no meio do padrão.
    // Estavam cobertos apenas para o papel de frontend — o de backend, que é o que mais claim
    // recebe, não tinha nenhuma trava testada contra saída do ScopeClaim.
    [InlineData("../src/**")]
    [InlineData("/etc/passwd")]
    [InlineData("/Users/mateus/**")]
    [InlineData("src/../../outro-projeto/**")]
    [InlineData("src/../governance/core.md")]
    // Imutabilidade de guardrail: o canon e a varredura que o engloba são negados para TODO papel.
    [InlineData("governance/**")]
    [InlineData("governance/core.md")]
    [InlineData("governance/manifest.yaml")]
    public void BackendRejectsFrontendAndOverbroadClaims(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Backend, [claim]);

        Assert.False(result.Allowed);
    }

    // O claim que o papel de crítico JÁ declarava e que esta política não conhecia. Enquanto ele
    // caía em `Backend`, todo parecer do Conselho era recusado em milissegundos — e como o
    // Conselho antecede o Desenvolvimento, a fase 4 nunca fechava. A mensagem visível era
    // `council.incomplete`, que descreve o sintoma e esconde a causa.
    [Theory]
    [InlineData("docs/conselho/**")]
    [InlineData("docs/conselho/playbook-tech-lead-ciclo-1.md")]
    public void CriticAcceptsOnlyItsOwnCouncilArea(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Critic, [claim]);

        Assert.True(result.Allowed);
        Assert.Equal("agent_path_scope_allowed", result.Code);
    }

    // A independência do parecer é a razão de o papel existir: quem opina não toca no que revisa,
    // e não ganha código de produção junto.
    [Theory]
    [InlineData("src/**")]
    [InlineData("docs/architecture/**")]
    [InlineData("frontend/**")]
    [InlineData("docs/**")]
    [InlineData("governance/core.md")]
    public void CriticRejectsEverythingOutsideTheCouncilArea(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Critic, [claim]);

        Assert.False(result.Allowed);
    }

    // A recíproca: a área do conselho não pertence a quem é revisado. Sem isso, um card comum
    // poderia escrever o próprio parecer favorável.
    [Theory]
    [InlineData("docs/conselho/**")]
    [InlineData("docs/conselho/playbook-qa-ciclo-1.md")]
    public void BackendCannotWriteInTheCouncilArea(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.Backend, [claim]);

        Assert.False(result.Allowed);
    }

    // A tradução papel→escopo estava copiada como ternário em seis arquivos, e foi por isso que o
    // papel de crítico caiu calado no `Backend` em todos eles.
    [Theory]
    [InlineData("critic", AgentPathScopeKind.Critic)]
    [InlineData("frontend-specialist", AgentPathScopeKind.FrontendSpecialist)]
    [InlineData("backend-specialist", AgentPathScopeKind.Backend)]
    [InlineData("chief-orchestrator", AgentPathScopeKind.Backend)]
    [InlineData(null, AgentPathScopeKind.Backend)]
    public void KindForRoleTranslatesTheLogicalRole(string? role, AgentPathScopeKind expected)
    {
        Assert.Equal(expected, AgentPathScopePolicy.KindForRole(role));
    }

    [Theory]
    [InlineData("src/**")]
    [InlineData("frontend/**")]
    [InlineData("tests/**")]
    [InlineData("infra/**")]
    [InlineData("tools/**")]
    [InlineData("docs/product/**")]
    [InlineData("docs/architecture/**")]
    [InlineData("docs/frontend/**")]
    public void ProjectExecutorOwnsTheWholeProductRepository(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.ProjectExecutor, [claim]);

        Assert.True(result.Allowed);
    }

    [Theory]
    // As duas exceções inegociáveis sobrevivem ao papel mais largo: o parecer não pertence a
    // quem executa, e a fonte canônica não pertence a card nenhum.
    [InlineData("docs/conselho/**")]
    [InlineData("docs/**")]
    [InlineData("governance/**")]
    [InlineData("governance/core.md")]
    [InlineData("governance/manifest.yaml")]
    public void ProjectExecutorStillCannotTouchCouncilOrCanon(string claim)
    {
        var result = AgentPathScopePolicy.Evaluate(AgentPathScopeKind.ProjectExecutor, [claim]);

        Assert.False(result.Allowed);
    }
}
