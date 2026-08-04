using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Accounts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A heurística inicial de planejamento do chefe: mapeia a demanda para o profissional certo
/// (persona do catálogo) e o papel de escopo. Simples e melhorável; um card explícito manda.
/// </summary>
public sealed class ChiefCardResolverTests
{
    private static ChiefCardResolution Resolve(string title, string body) =>
        ChiefCardResolver.Resolve(title, body, ["ok"], "medium");

    [Fact]
    public void FrontendWorkGoesToTheEngineerWithFrontendScope()
    {
        var r = Resolve("Ajustar o componente de login", "Corrigir o layout da tela em React.");
        Assert.Equal(AgentRoles.FrontendSpecialist, r.Role);
        Assert.Equal(ChiefCardResolver.Engineer, r.PersonaKey);
        Assert.Contains("frontend/**", r.ScopeClaims);
    }

    /// <summary>
    /// "componente" é vocabulário de arquitetura antes de ser de interface — o próprio C4 tem
    /// um nível chamado Componente. Enquanto ele valeu como sinal de frontend, os cards da
    /// Fase 3 (SAD, C4, ADRs, DER) resolviam para `frontend-specialist`; como só uma conta
    /// serve esse papel, a fase inteira parou quando aquela conta caiu — `role_not_allowed`
    /// em todas as outras, 7 cards presos em `ready`.
    /// </summary>
    [Fact]
    public void ArchitectureComponentsDoNotTurnACardIntoFrontendWork()
    {
        var r = Resolve(
            "3-Arquitetura — C4 (Contexto e Contêiner)",
            "Descrever os componentes do sistema e suas fronteiras.");

        Assert.Equal(AgentRoles.BackendSpecialist, r.Role);
        Assert.Equal(ChiefCardResolver.Architect, r.PersonaKey);
    }

    /// <summary>
    /// Um SAD de quatro mil caracteres cita "tela" uma vez. Enquanto uma única ocorrência num
    /// texto longo decidia o papel, o card de arquitetura inteiro virava trabalho de frontend.
    /// A especialização de frontend é para quem vai MEXER na interface, não para quem a
    /// descreve — e o papel passa a seguir o mesmo enquadramento que já escolhia a persona.
    /// </summary>
    [Fact]
    public void OneMentionOfAScreenDoesNotTurnAnArchitectureDocumentIntoFrontendWork()
    {
        var r = Resolve(
            "3-Arquitetura — SAD Ideal e Restrito",
            "Descrever a arquitetura do sistema, suas fronteiras e integrações. " +
            "O fluxo começa na tela de cadastro do equipamento.");

        Assert.Equal(AgentRoles.BackendSpecialist, r.Role);
        Assert.Equal(ChiefCardResolver.Architect, r.PersonaKey);
    }

    [Fact]
    public void ArchitectureWorkGoesToTheArchitect()
    {
        var r = Resolve("Definir a fronteira do módulo", "Escrever um ADR sobre a decisão técnica.");
        Assert.Equal(ChiefCardResolver.Architect, r.PersonaKey);
        Assert.Equal(AgentRoles.BackendSpecialist, r.Role);
    }

    [Fact]
    public void DocumentationWorkGoesToTheTechnicalWriter()
    {
        var r = Resolve("Atualizar o guia de operações", "Documentar o novo fluxo no manual.");
        Assert.Equal(ChiefCardResolver.TechnicalWriter, r.PersonaKey);
    }

    [Fact]
    public void GenericBackendWorkGoesToTheEngineerWithBackendScope()
    {
        var r = Resolve("Implementar auto-key versionado", "Adicionar a geração de chave no store.");
        Assert.Equal(AgentRoles.BackendSpecialist, r.Role);
        Assert.Equal(ChiefCardResolver.Engineer, r.PersonaKey);
        Assert.DoesNotContain("frontend/**", r.ScopeClaims);
    }

    [Fact]
    public void AnExplicitPersonaAndRoleOverrideTheHeuristic()
    {
        var r = ChiefCardResolver.Resolve(
            "Qualquer título", "Qualquer corpo", ["ok"], "high",
            explicitPersonaKey: ChiefCardResolver.CriticQa, explicitRole: AgentRoles.Critic);
        Assert.Equal(ChiefCardResolver.CriticQa, r.PersonaKey);
        Assert.Equal(AgentRoles.Critic, r.Role);
        Assert.Equal("review", r.RequiredCapability);
    }

    [Fact]
    public void TheResolutionCarriesTheDemandReadyForTheBriefing()
    {
        var r = ChiefCardResolver.Resolve(
            "Fatia P1", "Implementar X.", ["Testes verdes", "Sem drift"], "medium");
        Assert.Equal("Fatia P1", r.Card.Title);
        Assert.Equal(["Testes verdes", "Sem drift"], r.Card.AcceptanceCriteria);
        Assert.Equal("medium", r.Card.RiskTier);
    }

    [Fact]
    public void TheSpecialtyDeclaredByTheChiefBeatsTheKeywordHeuristic()
    {
        // Sem a declaração, "endpoint de sessão" cai no engenheiro genérico: a heurística conhece
        // 5 personas e o catálogo tem 25. A declaração do chefe alcança o especialista real.
        var r = Resolve(
            "Revisar a autenticação",
            "Papel exigido: backend-specialist\nEspecialidade exigida: architecture-security\n\nRevisar o endpoint de sessão.");
        Assert.Equal("architecture-security", r.PersonaKey);
        Assert.Equal(ChiefCardResolver.Engineer, r.InferredPersonaKey);
    }

    [Fact]
    public void OperationalCapabilityDoesNotMasqueradeAsTheProfessionalsRole()
    {
        var r = Resolve(
            "Produzir ficha de demanda",
            "Capacidade de execução autorizada: critic\n" +
            "Especialidade exigida: playbook-product-owner\n\nRevisar requisitos.");

        Assert.Equal(AgentRoles.Critic, r.Role);
        Assert.Equal("playbook-product-owner", r.PersonaKey);
    }

    [Fact]
    public void WithoutADeclarationTheInferredPersonaIsAlsoTheChosenOne()
    {
        var r = Resolve("Atualizar o guia de operações", "Documentar o novo fluxo no manual.");
        Assert.Equal(ChiefCardResolver.TechnicalWriter, r.PersonaKey);
        Assert.Equal(r.PersonaKey, r.InferredPersonaKey);
    }

    [Fact]
    public void AnAbsurdlyLongSpecialtyIsIgnoredAndTheHeuristicRemains()
    {
        var r = Resolve(
            "Implementar X",
            $"Especialidade exigida: {new string('x', 101)}\n\nAdicionar a geração de chave no store.");
        Assert.Equal(ChiefCardResolver.Engineer, r.PersonaKey);
    }

    [Fact]
    public void AnExplicitPersonaStillBeatsTheDeclarationInTheInstruction()
    {
        var r = ChiefCardResolver.Resolve(
            "Qualquer título",
            "Especialidade exigida: architecture-security",
            ["ok"],
            "high",
            explicitPersonaKey: ChiefCardResolver.CriticQa);
        Assert.Equal(ChiefCardResolver.CriticQa, r.PersonaKey);
    }

    /// <summary>
    /// F-17: os escopos declarados pela persona devem restringir o escopo do papel. Um engenheiro
    /// cujos AllowedScopes não incluem `docs/decisions/**` não deve receber claim naquela área,
    /// mesmo que o papel backend a permita.
    /// </summary>
    [Fact]
    public void PersonaAllowedScopesRestrictRoleScope()
    {
        var r = ChiefCardResolver.Resolve(
            "Implementar X",
            "Adicionar geração de chave no store.",
            ["ok"],
            "medium",
            personaAllowedScopes: ["src/**", "tests/**"],
            personaDeniedScopes: []);

        Assert.Contains("src/**", r.ScopeClaims);
        Assert.DoesNotContain("docs/decisions/**", r.ScopeClaims);
        Assert.DoesNotContain("docs/architecture/**", r.ScopeClaims);
    }

    /// <summary>
    /// F-17: os DeniedScopes da persona removem claims do papel. Um arquiteto que nega `infra/**`
    /// não deve ver essa raiz entre seus claims.
    /// </summary>
    [Fact]
    public void PersonaDeniedScopesRemoveRoleClaims()
    {
        var r = ChiefCardResolver.Resolve(
            "Definir fronteira",
            "Escrever um ADR sobre a decisão técnica.",
            ["ok"],
            "medium",
            personaAllowedScopes: [],
            personaDeniedScopes: ["infra/**"]);

        Assert.DoesNotContain("infra/**", r.ScopeClaims);
    }

    /// <summary>
    /// F-17: a persona pode reduzir o escopo, mas nunca ampliar. Um claim solicitado fora do
    /// escopo do papel é ignorado, e o planejador continua partindo do teto do papel.
    /// </summary>
    [Fact]
    public void PersonaCannotExpandBeyondRoleScope()
    {
        var r = ChiefCardResolver.Resolve(
            "Ajustar o componente de login",
            "Corrigir o layout da tela em React.",
            ["ok"],
            "medium",
            explicitRole: AgentRoles.FrontendSpecialist,
            personaAllowedScopes: ["frontend/**", "src/**"]);

        Assert.Contains("frontend/**", r.ScopeClaims);
        Assert.DoesNotContain("src/**", r.ScopeClaims);
    }

    /// <summary>
    /// F-17: se a persona restringir tudo (configuração inconsistente), voltamos ao escopo do
    /// papel para que a recusa seja auditável, em vez de produzir um card sem escopo silencioso.
    /// </summary>
    [Fact]
    public void OverlyRestrictivePersonaFallsBackToRoleScope()
    {
        var r = ChiefCardResolver.Resolve(
            "Implementar X",
            "Adicionar geração de chave no store.",
            ["ok"],
            "medium",
            personaAllowedScopes: ["governance/**"]);

        Assert.Contains("src/**", r.ScopeClaims);
    }
}
