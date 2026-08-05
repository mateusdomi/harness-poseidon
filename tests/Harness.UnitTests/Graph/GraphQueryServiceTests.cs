using Harness.Modules.Workflows.Product.Graph;
using Harness.SharedKernel.Graph;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 3 — as consultas da chefe (compactas, determinísticas, com teto declarado) e o digest de
/// impacto. A Bruna consulta o grafo; nunca o recebe bruto.
/// </summary>
public sealed class GraphQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static GraphNode Node(
        string id, GraphNodeType type, string title,
        GraphNodeState state = GraphNodeState.Active,
        string? causeId = null, int? causeVersion = null) =>
        new(id, "01ARZ3NDEKTSV4RRFFQ69G5FAV", type, id.Split(':')[1], "teste", 1, state,
            GraphProvenance.Deterministic, 1.0, title, Now, Now, causeId, causeVersion);

    private static readonly GraphNode[] Nodes =
    [
        Node("requirement:r1", GraphNodeType.Requirement, "Fronteira do escopo"),
        Node(
            "artifact:plano", GraphNodeType.Artifact, "Plano de testes",
            GraphNodeState.Stale, "requirement:r1", 2),
        Node("card:review", GraphNodeType.Card, "Code review"),
        Node("card:c00", GraphNodeType.Card, "C-00 fundação"),
        Node("gate:fase6", GraphNodeType.Gate, "Quality gate fase 6"),
        Node("evidence:e1", GraphNodeType.Evidence, "Suíte verde"),
    ];

    private static readonly GraphEdge[] Edges =
    [
        GraphEdge.Structural("card:review", "card:c00", GraphRelationType.DependsOn, Now),
        GraphEdge.Structural("card:review", "artifact:plano", GraphRelationType.DerivesFrom, Now),
        GraphEdge.Structural("gate:fase6", "artifact:plano", GraphRelationType.Requires, Now),
        GraphEdge.Structural("gate:fase6", "card:c00", GraphRelationType.Requires, Now),
        GraphEdge.Structural("evidence:e1", "card:c00", GraphRelationType.Proves, Now),
    ];

    [Fact]
    public void ACadeiaDeBloqueioChegaACausaRaizEMarcaOStale()
    {
        var chain = GraphQueryService.GetBlockingChain(Nodes, Edges, "card:review");

        Assert.Contains("C-00 fundação", chain, StringComparison.Ordinal);
        Assert.Contains("Plano de testes", chain, StringComparison.Ordinal);
        Assert.Contains("[STALE — causa: requirement:r1 v2]", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void OImpactoDownstreamListaClassificacaoECaminho()
    {
        var impact = GraphQueryService.GetDownstreamImpact(Nodes, Edges, "requirement:r1");

        // requirement:r1 não tem aresta de impacto neste grafo além do STALE já aplicado — o
        // plano deriva? Não: plano é alvo de derives_from a partir do review. r1 não é atingível.
        Assert.Contains("não afeta nenhum outro item", impact, StringComparison.Ordinal);

        var fromC00 = GraphQueryService.GetDownstreamImpact(Nodes, Edges, "card:c00");
        Assert.Contains("Code review", fromC00, StringComparison.Ordinal);
        Assert.Contains("DIRETO", fromC00, StringComparison.Ordinal);
        Assert.Contains("card:c00 → card:review", fromC00, StringComparison.Ordinal);
    }

    [Fact]
    public void OGateMostraOQueTemProvaEOQueFalta()
    {
        var gaps = GraphQueryService.GetGateEvidenceGaps(Nodes, Edges, "gate:fase6");

        Assert.Contains("OK: C-00 fundação", gaps, StringComparison.Ordinal);
        Assert.Contains("FALTA: Plano de testes", gaps, StringComparison.Ordinal);
        Assert.Contains("STALE, precisa de revalidação", gaps, StringComparison.Ordinal);
    }

    [Fact]
    public void OTrabalhoStaleListaACausaLegivel()
    {
        var stale = GraphQueryService.GetStaleWork(Nodes);

        Assert.Contains("1 item(ns) aguardando revalidação", stale, StringComparison.Ordinal);
        Assert.Contains(
            "Plano de testes (artifact:plano) — invalidado por \"Fronteira do escopo\" v2",
            stale, StringComparison.Ordinal);
    }

    [Fact]
    public void NoInexistenteEDeclaradoNuncaInventado()
    {
        Assert.Contains(
            "não existe no grafo",
            GraphQueryService.GetBlockingChain(Nodes, Edges, "card:fantasma"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ODigestPoeODeltaPrimeiroERespeitaOOrcamento()
    {
        var digest = ProjectImpactDigests.Build(7, Nodes, Edges, Now.AddHours(-1));

        Assert.Contains("Grafo do projeto v7", digest, StringComparison.Ordinal);
        Assert.Contains("MUDANÇAS DESDE O ÚLTIMO TURNO", digest, StringComparison.Ordinal);
        Assert.Contains("AGUARDANDO REVALIDAÇÃO", digest, StringComparison.Ordinal);
        Assert.True(digest.Length <= 12_200, $"digest com {digest.Length} chars estoura o orçamento.");

        var empty = ProjectImpactDigests.Build(0, [], [], null);
        Assert.Contains("não tem nós", empty, StringComparison.Ordinal);
    }
}
