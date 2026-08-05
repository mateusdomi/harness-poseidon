using Harness.Modules.Workflows.Product.Graph;
using Harness.SharedKernel.Graph;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 4.2 — as evals 2 e 4 da suíte de impacto (as evals 1, 3 e 5 vivem em
/// <see cref="ReplayProofTests"/> e <see cref="ImpactAnalysisTests"/>).
/// </summary>
public sealed class ImpactEvalTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static GraphNode Node(string id, GraphNodeType type, string title) =>
        new(id, "01ARZ3NDEKTSV4RRFFQ69G5FAV", type, id.Split(':')[1], "teste", 1,
            GraphNodeState.Active, GraphProvenance.Deterministic, 1.0, title, Now, Now);

    /// <summary>
    /// Eval 2 — a volumetria muda (50 → 20.000 simultâneos): a NFR atinge a arquitetura que ela
    /// restringe, o teste de performance que a verifica e o card de capacity que dela deriva.
    /// A interface, que não declara vínculo com a NFR, fica INTOCADA.
    /// </summary>
    [Fact]
    public void Eval2MudancaDeVolumetriaAtingeArquiteturaPerformanceECapacityMasNaoAUi()
    {
        var nodes = new[]
        {
            Node("nfr:volumetria", GraphNodeType.Nfr, "50 usuários simultâneos"),
            Node("artifact:arquitetura", GraphNodeType.Artifact, "Documento de arquitetura"),
            Node("test:performance", GraphNodeType.Test, "Teste de carga"),
            Node("card:capacity", GraphNodeType.Card, "Plano de capacity"),
            Node("card:telas", GraphNodeType.Card, "Telas do sistema"),
        };
        var edges = new[]
        {
            GraphEdge.Structural(
                "artifact:arquitetura", "nfr:volumetria", GraphRelationType.ConstrainedBy, Now),
            GraphEdge.Structural(
                "test:performance", "nfr:volumetria", GraphRelationType.Verifies, Now),
            GraphEdge.Structural(
                "card:capacity", "nfr:volumetria", GraphRelationType.DerivesFrom, Now),
        };

        var impacted = ImpactAnalysisService.Analyze(nodes, edges, "nfr:volumetria");

        Assert.Equal(3, impacted.Count);
        Assert.All(impacted, node => Assert.Equal(ImpactClassification.Direct, node.Classification));
        Assert.Contains(impacted, node => node.NodeId == "artifact:arquitetura");
        Assert.Contains(impacted, node => node.NodeId == "test:performance");
        Assert.Contains(impacted, node => node.NodeId == "card:capacity");
        Assert.DoesNotContain(impacted, node => node.NodeId == "card:telas");

        // E vira trabalho de revalidação nomeando os três — nunca ação destrutiva.
        var work = GraphRevalidationWorks.From(
            nodes[0] with { Version = 2, Title = "20.000 usuários simultâneos" },
            impacted,
            nodes.ToDictionary(node => node.Id, StringComparer.Ordinal));
        Assert.NotNull(work);
        Assert.Contains("Documento de arquitetura", work.Instruction, StringComparison.Ordinal);
        Assert.Contains("Teste de carga", work.Instruction, StringComparison.Ordinal);
        Assert.Contains("Plano de capacity", work.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// Eval 4 — requisito adicionado TARDE: entra no grafo sem perturbar nada (nenhum STALE em
    /// quem já existia), e a cadeia requirement → card → teste → evidência é EXIGIDA antes do
    /// Done — o gate declara FALTA até a evidência aparecer, e some quando ela prova.
    /// </summary>
    [Fact]
    public void Eval4RequisitoTardioNaoPerturbaNadaEExigeACadeiaCompletaAntesDoDone()
    {
        var existing = new[]
        {
            Node("requirement:r1", GraphNodeType.Requirement, "Requisito original"),
            Node("card:c1", GraphNodeType.Card, "Card original"),
            Node("gate:entrega", GraphNodeType.Gate, "Gate de entrega"),
        };
        var existingEdges = new[]
        {
            GraphEdge.Structural("card:c1", "requirement:r1", GraphRelationType.Implements, Now),
            GraphEdge.Structural("gate:entrega", "requirement:r1", GraphRelationType.Requires, Now),
        };

        // O requisito tardio chega: nó novo, exigência nova do gate. O ÚNICO atingido é o
        // gate que passou a exigi-lo — sinal desejado, não ruído. O trabalho pré-existente
        // (requisito original, card original) fica intocado: nenhum STALE espúrio.
        var late = Node("requirement:tardio", GraphNodeType.Requirement, "Requisito tardio");
        var nodes = existing.Append(late).ToArray();
        var edges = existingEdges.Append(
            GraphEdge.Structural("gate:entrega", "requirement:tardio", GraphRelationType.Requires, Now))
            .ToArray();
        var impacted = ImpactAnalysisService.Analyze(nodes, edges, "requirement:tardio");
        var hit = Assert.Single(impacted);
        Assert.Equal("gate:entrega", hit.NodeId);
        Assert.DoesNotContain(impacted, node => node.NodeId is "requirement:r1" or "card:c1");

        // Sem card nem evidência, o gate declara a FALTA — o requisito tardio não pode ser
        // esquecido a caminho do Done.
        var gaps = GraphQueryService.GetGateEvidenceGaps(nodes, edges, "gate:entrega");
        Assert.Contains("FALTA: Requisito tardio", gaps, StringComparison.Ordinal);

        // A cadeia completa chega: card implementa, teste verifica, evidência prova. Só então o
        // gate dá OK ao requisito tardio.
        var withChain = nodes
            .Append(Node("card:tardio", GraphNodeType.Card, "Card do requisito tardio"))
            .Append(Node("test:tardio", GraphNodeType.Test, "Teste do requisito tardio"))
            .Append(Node("evidence:tardia", GraphNodeType.Evidence, "Suíte verde do tardio"))
            .ToArray();
        var withChainEdges = edges
            .Append(GraphEdge.Structural(
                "card:tardio", "requirement:tardio", GraphRelationType.Implements, Now))
            .Append(GraphEdge.Structural(
                "test:tardio", "requirement:tardio", GraphRelationType.Verifies, Now))
            .Append(GraphEdge.Structural(
                "evidence:tardia", "requirement:tardio", GraphRelationType.Proves, Now))
            .ToArray();

        var satisfied = GraphQueryService.GetGateEvidenceGaps(withChain, withChainEdges, "gate:entrega");
        Assert.Contains("OK: Requisito tardio", satisfied, StringComparison.Ordinal);
        Assert.DoesNotContain("FALTA: Requisito tardio", satisfied, StringComparison.Ordinal);
    }
}
