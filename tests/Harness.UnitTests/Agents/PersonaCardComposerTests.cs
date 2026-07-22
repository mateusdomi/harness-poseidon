using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O briefing do agente é PERSONA (do catálogo: quem/como) + CARD (a demanda: o quê/critérios).
/// A persona nunca é reescrita no card — vem do profissional especializado. É determinístico,
/// para o operador comparar "card X → resultado Y" e iterar o modelo.
/// </summary>
public sealed class PersonaCardComposerTests
{
    private static AgentDefinitionContent Architect() => new(
        "software-architect", "Software Architect", "specialist", "Arquitetura de software",
        "Define arquitetura e limites.", null, [], [],
        Persona: "Você pensa em limites, atributos de qualidade e decisões duráveis.",
        Mission: "Proteger a integridade arquitetural do sistema.",
        OperatingPrinciples: ["Decisão reversível primeiro", "Documente o porquê"],
        Deliverables: ["ADR", "diagrama de contexto"],
        QualityCriteria: ["Sem acoplamento novo", "Trade-offs explícitos"],
        CommunicationStyle: "Objetivo e fundamentado.",
        Limitations: ["Não implementa UI"]);

    private static DelegationCard Card() => new(
        "Definir a fronteira do módulo de cobrança",
        "Separar cobrança de pedidos; propor a interface de integração.",
        ["A fronteira está documentada num ADR", "Sem dependência circular"],
        ["docs/decisions/**", "docs/architecture/**"],
        "medium");

    [Fact]
    public void TheBriefingCarriesThePersonaThenTheDemand()
    {
        var briefing = PersonaCardComposer.Compose(Architect(), Card());

        // Persona (quem/como) vem primeiro e do catálogo.
        Assert.Contains("# Você é: Software Architect — Arquitetura de software", briefing, StringComparison.Ordinal);
        Assert.Contains("Você pensa em limites", briefing, StringComparison.Ordinal);
        Assert.Contains("## Sua missão", briefing, StringComparison.Ordinal);
        Assert.Contains("- ADR", briefing, StringComparison.Ordinal);
        Assert.Contains("- Trade-offs explícitos", briefing, StringComparison.Ordinal);

        // Depois a demanda (o quê/critérios), do card.
        Assert.Contains("# Demanda: Definir a fronteira do módulo de cobrança", briefing, StringComparison.Ordinal);
        Assert.Contains("## Critérios de aceite desta demanda", briefing, StringComparison.Ordinal);
        Assert.Contains("- A fronteira está documentada num ADR", briefing, StringComparison.Ordinal);
        Assert.Contains("docs/architecture/**", briefing, StringComparison.Ordinal);

        // Persona ANTES da demanda.
        Assert.True(
            briefing.IndexOf("# Você é:", StringComparison.Ordinal) <
            briefing.IndexOf("# Demanda:", StringComparison.Ordinal));
    }

    [Fact]
    public void ItIsDeterministicForTheSameInputs()
    {
        Assert.Equal(
            PersonaCardComposer.Compose(Architect(), Card()),
            PersonaCardComposer.Compose(Architect(), Card()));
    }

    [Fact]
    public void EmptyPersonaSectionsAreOmittedInsteadOfPrintingEmptyHeadings()
    {
        var sparse = new AgentDefinitionContent(
            "software-engineer", "Software Engineer", "specialist", null,
            "Implementa fatias pequenas e testadas.", null, [], [],
            Persona: null, Mission: null, OperatingPrinciples: [], Deliverables: [],
            QualityCriteria: [], CommunicationStyle: null, Limitations: []);

        var briefing = PersonaCardComposer.Compose(sparse, Card());

        // Sem missão/princípios/etc., os cabeçalhos não aparecem vazios; a descrição serve de persona.
        Assert.DoesNotContain("## Sua missão", briefing, StringComparison.Ordinal);
        Assert.DoesNotContain("## Princípios de operação", briefing, StringComparison.Ordinal);
        Assert.Contains("Implementa fatias pequenas e testadas.", briefing, StringComparison.Ordinal);
        Assert.Contains("# Demanda:", briefing, StringComparison.Ordinal);
    }
}
