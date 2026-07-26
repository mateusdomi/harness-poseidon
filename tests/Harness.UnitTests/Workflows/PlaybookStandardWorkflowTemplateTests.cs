using Harness.Host.Workflows;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// A esteira de 9 fases do playbook é a 2ª fonte da verdade do projeto: ela precisa existir no
/// catálogo como DADOS, com os nomes e a ordem exatos, cada fase carregando gate Default-FAIL e
/// artefatos de saída. Este teste é a trava contra deriva silenciosa entre playbook e produto.
/// </summary>
public sealed class PlaybookStandardWorkflowTemplateTests
{
    [Fact]
    public void CatalogCarriesTheNinePlaybookPhasesInOrder()
    {
        var template = CanonicalWorkflowTemplates.PlaybookStandardTemplate;

        Assert.Equal("playbook-standard", template.Key);
        Assert.Equal(
        [
            "1-Triagem", "2-Descoberta", "3-Arquitetura", "4-Planejamento", "5-Desenvolvimento",
            "6-Testes", "7-Homologação", "8-Release", "9-Sustentação",
        ], template.Phases);
    }

    [Fact]
    public void EveryPhaseHasAGateAndOutputArtifacts()
    {
        var template = CanonicalWorkflowTemplates.PlaybookStandardTemplate;

        // Default-FAIL: nenhuma fase transita sem gate, e nenhuma fase termina sem artefato.
        Assert.All(template.Phases, phase =>
        {
            Assert.True(
                template.GatesByPhase.ContainsKey(phase),
                $"A fase '{phase}' precisa de um gate — transição de fase é card gate aprovado.");
            Assert.NotEmpty(template.GatesByPhase[phase]);
            Assert.True(
                template.DocumentsByPhase.ContainsKey(phase),
                $"A fase '{phase}' precisa declarar seus artefatos de saída.");
            Assert.NotEmpty(template.DocumentsByPhase[phase]);
        });
    }

    [Theory]
    // Os critérios objetivos que o playbook fixa em cada gate; mudá-los é mudar o processo.
    [InlineData("1-Triagem", "criticidade")]
    [InlineData("2-Descoberta", "INVEST")]
    [InlineData("3-Arquitetura", "revisor distinto")]
    [InlineData("4-Planejamento", "risk_tier")]
    [InlineData("5-Desenvolvimento", "agente distinto")]
    [InlineData("6-Testes", "P0/P1")]
    [InlineData("7-Homologação", "HITL")]
    [InlineData("8-Release", "rollback testado")]
    [InlineData("9-Sustentação", "error budget")]
    public void GatesCarryThePlaybookAcceptanceCriteria(string phase, string criterion)
    {
        var gate = Assert.Single(
            CanonicalWorkflowTemplates.PlaybookStandardTemplate.GatesByPhase[phase]);

        Assert.Contains(criterion, gate, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Artefatos que o playbook nomeia como saída obrigatória das fases de decisão e aceite.
    [InlineData("1-Triagem", "Ficha de Demanda Qualificada")]
    [InlineData("2-Descoberta", "PRD")]
    [InlineData("3-Arquitetura", "ADRs")]
    [InlineData("6-Testes", "Parecer Go/No-Go")]
    [InlineData("7-Homologação", "Termo de Aceite")]
    [InlineData("8-Release", "GMUD")]
    [InlineData("9-Sustentação", "Postmortem blameless")]
    public void PhasesDeclareThePlaybookArtifacts(string phase, string artifact)
    {
        Assert.Contains(
            artifact,
            CanonicalWorkflowTemplates.PlaybookStandardTemplate.DocumentsByPhase[phase]);
    }

    [Fact]
    public void TemplateIsSeededAlongsideTheOtherCanonicalTemplatesWithoutDisplacingTheRecommended()
    {
        Assert.Contains(
            CanonicalWorkflowTemplates.PlaybookStandardTemplate,
            CanonicalWorkflowTemplates.All);
        // A pré-seleção de projeto novo continua no template recomendado: acrescentar a esteira
        // do playbook ao catálogo não muda o caminho dourado existente.
        Assert.Equal("delivery-standard", CanonicalWorkflowTemplates.Recommended.Key);
        Assert.Single(
            CanonicalWorkflowTemplates.All,
            template => template.Key == CanonicalWorkflowTemplates.PlaybookStandardKey);
    }
}
