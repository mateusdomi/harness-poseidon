using Harness.Modules.Workflows.Product.Graph;
using Harness.SharedKernel.Graph;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 1 — os invariantes do grafo e a equivalência rebuild ≡ incremental.
///
/// A equivalência não é um teste de sorte: ids determinísticos + ordenação total + evento que
/// carrega o estado COMPLETO do item tornam qualquer divergência um defeito de construção. O
/// teste existe para o dia em que alguém "otimizar" um desses três pilares.
/// </summary>
public sealed class ProjectGraphProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public void ArestaEstruturalEDeterministicComConfiancaTotalEAceita()
    {
        var edge = GraphEdge.Structural("card:c1", "requirement:r1", GraphRelationType.Implements, Now);

        Assert.Equal(GraphProvenance.Deterministic, edge.Provenance);
        Assert.Equal(1.0, edge.Confidence);
        Assert.Equal(GraphEdgeStatus.Accepted, edge.Status);
    }

    [Fact]
    public void ArestaInferidaNasceProposedENuncaAccepted()
    {
        var edge = GraphEdge.Proposed("card:c1", "risk:x", GraphRelationType.Impacts, 0.7, Now);

        Assert.Equal(GraphProvenance.ModelInference, edge.Provenance);
        Assert.Equal(GraphEdgeStatus.Proposed, edge.Status);
        Assert.Equal(0.7, edge.Confidence);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GraphEdge.Proposed("a", "b", GraphRelationType.Impacts, 1.5, Now));
    }

    /// <summary>
    /// O invariante mais importante do modelo: NÃO EXISTE aresta genérica. O conjunto de
    /// relações é exatamente o fechado da missão — nem um "relates_to" a mais, nem um tipo a
    /// menos. Quem precisar de relação nova muda a MISSÃO, não este teste.
    /// </summary>
    [Fact]
    public void OsConjuntosDeTiposSaoExatamenteOsFechadosDaMissao()
    {
        Assert.Equal(
            ["Implements", "Verifies", "Proves", "DerivesFrom", "ConstrainedBy", "Impacts",
             "DependsOn", "BlockedBy", "Supersedes", "Invalidates", "Requires", "Produces"],
            Enum.GetNames<GraphRelationType>());
        Assert.DoesNotContain(
            Enum.GetNames<GraphRelationType>(),
            name => name.Contains("Relate", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Generic", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            ["Requirement", "Nfr", "HumanFact", "Constraint", "Decision", "Risk", "OpenQuestion",
             "Artifact", "Phase", "Gate", "Card", "Test", "Evidence"],
            Enum.GetNames<GraphNodeType>());
    }

    [Fact]
    public void ProjetarOMesmoSnapshotDuasVezesProduzOMesmoGrafo()
    {
        var snapshot = SampleSnapshot();

        var first = ProjectGraphProjector.Project(snapshot, Now);
        var second = ProjectGraphProjector.Project(snapshot, Now);

        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Edges, second.Edges);
    }

    [Fact]
    public void ArestaParaNoInexistenteFicaForaDeterministicamente()
    {
        var snapshot = new GraphSourceSnapshot(
            ProjectId,
            [new GraphSourceItem(GraphNodeType.Card, "c1", "work_task", 1, "Card 1")],
            [new GraphSourceLink(
                GraphRelationType.Implements,
                GraphNodeType.Card, "c1",
                GraphNodeType.Requirement, "fantasma")]);

        var projection = ProjectGraphProjector.Project(snapshot, Now);

        Assert.Single(projection.Nodes);
        Assert.Empty(projection.Edges);
    }

    /// <summary>
    /// A prova de equivalência da Onda 1: o snapshot final construído por EVENTOS incrementais
    /// projeta o MESMO grafo que a reconstrução direta do estado final — nó a nó, aresta a
    /// aresta, inclusive depois de upsert repetido (idempotência) e de aposentadoria.
    /// </summary>
    [Fact]
    public void RebuildEIncrementalProduzemGrafosIdenticos()
    {
        var initial = new GraphSourceSnapshot(ProjectId, [], []);
        var card = new GraphSourceItem(GraphNodeType.Card, "c1", "work_task", 1, "Implementar login");
        var requirement = new GraphSourceItem(
            GraphNodeType.Requirement, "r1", "demand", 1, "Login de usuário");
        var evidence = new GraphSourceItem(
            GraphNodeType.Evidence, "a1", "work_attempt", 1, "Tentativa 1 aprovada");

        var events = new GraphSourceEvent[]
        {
            new GraphItemUpserted(requirement, []),
            new GraphItemUpserted(card, [Link(GraphRelationType.Implements, card, requirement)]),
            // Upsert REPETIDO do mesmo item: idempotência sob reentrega de evento.
            new GraphItemUpserted(card, [Link(GraphRelationType.Implements, card, requirement)]),
            new GraphItemUpserted(evidence, [new GraphSourceLink(
                GraphRelationType.Proves,
                GraphNodeType.Evidence, "a1", GraphNodeType.Card, "c1")]),
            // O card muda de versão (instrução nova) e mantém o vínculo.
            new GraphItemUpserted(
                card with { Version = 2, Title = "Implementar login (v2)" },
                [Link(GraphRelationType.Implements, card, requirement)]),
            new GraphItemRetired(GraphNodeType.Requirement, "r1"),
        };

        var incrementalState = events.Aggregate(initial, ProjectGraphProjector.Apply);
        var incremental = ProjectGraphProjector.Project(incrementalState, Now);

        var rebuilt = ProjectGraphProjector.Project(
            new GraphSourceSnapshot(
                ProjectId,
                [requirement with { Retired = true },
                 card with { Version = 2, Title = "Implementar login (v2)" },
                 evidence],
                [Link(GraphRelationType.Implements, card, requirement),
                 new GraphSourceLink(
                     GraphRelationType.Proves,
                     GraphNodeType.Evidence, "a1", GraphNodeType.Card, "c1")]),
            Now);

        Assert.Equal(rebuilt.Nodes, incremental.Nodes);
        Assert.Equal(rebuilt.Edges, incremental.Edges);
    }

    [Fact]
    public void AposentarNaoApagaNemONoNemAsArestasDele()
    {
        var card = new GraphSourceItem(GraphNodeType.Card, "c1", "work_task", 1, "Card");
        var requirement = new GraphSourceItem(GraphNodeType.Requirement, "r1", "demand", 1, "Req");
        var state = new GraphSourceSnapshot(
            ProjectId, [card, requirement], [Link(GraphRelationType.Implements, card, requirement)]);

        var retired = ProjectGraphProjector.Apply(
            state, new GraphItemRetired(GraphNodeType.Card, "c1"));
        var projection = ProjectGraphProjector.Project(retired, Now);

        var node = Assert.Single(projection.Nodes, n => n.Type == GraphNodeType.Card);
        Assert.Equal(GraphNodeState.Retired, node.State);
        // A ARESTA fica: a história de por que algo dependia de algo é o que a perícia do run
        // de empréstimos não tinha.
        Assert.Single(projection.Edges);
    }

    private static GraphSourceLink Link(
        GraphRelationType relation, GraphSourceItem from, GraphSourceItem to) =>
        new(relation, from.Type, from.SourceId, to.Type, to.SourceId);

    private static GraphSourceSnapshot SampleSnapshot()
    {
        var requirement = new GraphSourceItem(GraphNodeType.Requirement, "r1", "demand", 1, "Req");
        var card = new GraphSourceItem(GraphNodeType.Card, "c1", "work_task", 1, "Card");
        return new GraphSourceSnapshot(
            ProjectId,
            [requirement, card],
            [Link(GraphRelationType.Implements, card, requirement)]);
    }
}
