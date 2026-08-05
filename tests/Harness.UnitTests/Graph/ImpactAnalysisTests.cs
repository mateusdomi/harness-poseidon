using Harness.Modules.Workflows.Product.Graph;
using Harness.SharedKernel.Graph;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 2 — travessia de impacto determinística: Direct/Transitive por aresta Accepted,
/// Possible quando o caminho atravessa Proposed (informativo, nunca STALE), e o controle de
/// falso positivo — mudança sem alcance estrutural não propaga NADA.
/// </summary>
public sealed class ImpactAnalysisTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static GraphNode Node(string id, GraphNodeType type, GraphNodeState state = GraphNodeState.Active) =>
        new(id, "01ARZ3NDEKTSV4RRFFQ69G5FAV", type, id.Split(':')[1], "teste", 1, state,
            GraphProvenance.Deterministic, 1.0, id, Now, Now);

    [Fact]
    public void MudancaNoRequisitoAtingeOCardDiretoEAEvidenciaTransitiva()
    {
        // card implementa requisito; evidência prova card. Muda o REQUISITO:
        // o card é Direct (1 salto contra a seta) e a evidência é Transitive (2 saltos).
        var nodes = new[]
        {
            Node("requirement:r1", GraphNodeType.Requirement),
            Node("card:c1", GraphNodeType.Card),
            Node("evidence:e1", GraphNodeType.Evidence),
        };
        var edges = new[]
        {
            GraphEdge.Structural("card:c1", "requirement:r1", GraphRelationType.Implements, Now),
            GraphEdge.Structural("evidence:e1", "card:c1", GraphRelationType.Proves, Now),
        };

        var impacted = ImpactAnalysisService.Analyze(nodes, edges, "requirement:r1");

        Assert.Equal(2, impacted.Count);
        var card = Assert.Single(impacted, node => node.NodeId == "card:c1");
        Assert.Equal(ImpactClassification.Direct, card.Classification);
        var evidence = Assert.Single(impacted, node => node.NodeId == "evidence:e1");
        Assert.Equal(ImpactClassification.Transitive, evidence.Classification);
        Assert.Equal(["requirement:r1", "card:c1", "evidence:e1"], evidence.Path);
    }

    [Fact]
    public void AlcancadoSoPorArestaProposedEPossible()
    {
        var nodes = new[]
        {
            Node("requirement:r1", GraphNodeType.Requirement),
            Node("card:c1", GraphNodeType.Card),
        };
        var edges = new[]
        {
            GraphEdge.Proposed("card:c1", "requirement:r1", GraphRelationType.Implements, 0.6, Now),
        };

        var impacted = ImpactAnalysisService.Analyze(nodes, edges, "requirement:r1");

        var card = Assert.Single(impacted);
        Assert.Equal(ImpactClassification.Possible, card.Classification);
    }

    [Fact]
    public void CaminhoAcceptedVenceCaminhoComProposedParaOMesmoNo()
    {
        // Dois caminhos até a evidência: um todo Accepted, outro passando por Proposed.
        // Vale o MELHOR: a classificação é estrutural (Transitive), não Possible.
        var nodes = new[]
        {
            Node("requirement:r1", GraphNodeType.Requirement),
            Node("card:c1", GraphNodeType.Card),
            Node("evidence:e1", GraphNodeType.Evidence),
        };
        var edges = new[]
        {
            GraphEdge.Structural("card:c1", "requirement:r1", GraphRelationType.Implements, Now),
            GraphEdge.Structural("evidence:e1", "card:c1", GraphRelationType.Proves, Now),
            GraphEdge.Proposed("evidence:e1", "requirement:r1", GraphRelationType.Verifies, 0.5, Now),
        };

        var impacted = ImpactAnalysisService.Analyze(nodes, edges, "requirement:r1");

        var evidence = Assert.Single(impacted, node => node.NodeId == "evidence:e1");
        Assert.Equal(ImpactClassification.Transitive, evidence.Classification);
    }

    /// <summary>
    /// Onda 2.4 — o controle de falso positivo: nó sem aresta de impacto não propaga nada.
    /// STALE ruidoso destrói a confiança no mecanismo; falso positivo é defeito da MESMA
    /// severidade que falso negativo. (É a Eval 5 da Onda 4, congelada aqui como unidade.)
    /// </summary>
    [Fact]
    public void MudancaEmNoSemArestasDeImpactoNaoPropagaNada()
    {
        var nodes = new[]
        {
            Node("openquestion:q1", GraphNodeType.OpenQuestion),
            Node("card:c1", GraphNodeType.Card),
            Node("requirement:r1", GraphNodeType.Requirement),
        };
        var edges = new[]
        {
            GraphEdge.Structural("card:c1", "requirement:r1", GraphRelationType.Implements, Now),
        };

        Assert.Empty(ImpactAnalysisService.Analyze(nodes, edges, "openquestion:q1"));
    }

    [Fact]
    public void ArestaExpiradaENoAposentadoNaoPropagam()
    {
        var nodes = new[]
        {
            Node("requirement:r1", GraphNodeType.Requirement),
            Node("card:c1", GraphNodeType.Card),
            Node("card:c2", GraphNodeType.Card, GraphNodeState.Retired),
        };
        var edges = new[]
        {
            GraphEdge.Structural("card:c1", "requirement:r1", GraphRelationType.Implements, Now)
                with { ValidUntil = Now.AddDays(-1) },
            GraphEdge.Structural("card:c2", "requirement:r1", GraphRelationType.Implements, Now),
        };

        Assert.Empty(ImpactAnalysisService.Analyze(nodes, edges, "requirement:r1"));
    }

    [Fact]
    public void DecisaoQueInvalidaPropagaAFavorDaSeta()
    {
        // decision:d2 invalidates artifact:a1 — a mudança na DECISÃO atinge o artefato.
        var nodes = new[]
        {
            Node("decision:d2", GraphNodeType.Decision),
            Node("artifact:a1", GraphNodeType.Artifact),
        };
        var edges = new[]
        {
            GraphEdge.Structural("decision:d2", "artifact:a1", GraphRelationType.Invalidates, Now),
        };

        var impacted = ImpactAnalysisService.Analyze(nodes, edges, "decision:d2");

        var artifact = Assert.Single(impacted);
        Assert.Equal("artifact:a1", artifact.NodeId);
        Assert.Equal(ImpactClassification.Direct, artifact.Classification);
    }
}
