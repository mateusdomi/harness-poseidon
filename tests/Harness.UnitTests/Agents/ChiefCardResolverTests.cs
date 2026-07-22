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
}
