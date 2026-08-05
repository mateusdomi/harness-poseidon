using Harness.Modules.Coordination.Application;
using Harness.Modules.Workflows.Product.Graph;
using Harness.SharedKernel.Graph;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 4 — AS PROVAS DE REPLAY, congeladas como testes permanentes sobre o arquivo REAL do run
/// QA-PROVA-LIMPA-20260802-EMPRESTIMOS (fixture aparada, ids/títulos/estados/timestamps fiéis).
///
/// Prova A: o flip-flop de escopo (ADR de fronteira backend-only às 11:37 de 04/08; reversão às
/// 13:36 com a fase de Testes já ativa) TEM de alcançar o plano de testes, o quality gate e o
/// parecer Go/No-Go — os três foram escritos sob a premissa amputada.
///
/// Prova B: o review despachado fora de ordem (23:48 de 03/08, com TODOS os cards de
/// implementação da fase 5 incompletos) TEM de ser recusado pelo readiness com a cadeia de
/// dependência na razão.
/// </summary>
public sealed class ReplayProofTests
{
    private static readonly Lazy<string> Archive = new(() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-emprestimos-qa-prova-limpa.json")));

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>O momento do ADR-0006 (reversão da fronteira), com a fase de Testes já ativa.</summary>
    private static readonly DateTimeOffset AposReversao =
        new(2026, 8, 4, 13, 40, 0, TimeSpan.FromHours(-3));

    /// <summary>O primeiro despacho do card de code review (03/08 23:48 UTC).</summary>
    private static readonly DateTimeOffset DespachoDoReview =
        new(2026, 8, 3, 23, 48, 0, TimeSpan.Zero);

    private const string AdrFronteira = "decision:01KZ690KDQY4R5HPZ0NRTH0MFA";

    [Fact]
    public void ProvaAOFlipFlopDeEscopoAlcancaPlanoDeTestesQualityGateEGoNoGo()
    {
        var snapshot = ProjectGraphReplayImporter.Import(Archive.Value, AposReversao);
        var projection = ProjectGraphProjector.Project(snapshot, Now);

        // A reversão (ADR-0006, 13:36) invalida a premissa da fronteira backend-only: a
        // propagação parte do ADR de fronteira, cuja constraint os artefatos da fase de Testes
        // carregavam (constrained_by — decisões restringem trabalho posterior).
        var impacted = ImpactAnalysisService.Analyze(
            projection.Nodes, projection.Edges, AdrFronteira);
        var blocking = impacted
            .Where(node => node.Classification != ImpactClassification.Possible)
            .Select(node => node.NodeId)
            .ToHashSet(StringComparer.Ordinal);

        string[] mandatory =
        [
            NodeIdByTitle(projection, "6-Testes — Plano de Testes"),
            NodeIdByTitle(projection, "6-Testes — Relatório de Quality Gate"),
            NodeIdByTitle(projection, "6-Testes — Parecer Go/No-Go"),
        ];
        foreach (var nodeId in mandatory)
        {
            Assert.Contains(nodeId, blocking);
        }

        // E o STALE vira TRABALHO visível: o card de revalidação derivado nomeia os três.
        var cause = projection.Nodes.Single(node => node.Id == AdrFronteira);
        var work = GraphRevalidationWorks.From(
            cause with { Version = 2 },
            impacted,
            projection.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal));
        Assert.NotNull(work);
        Assert.Contains("Plano de Testes", work.Instruction, StringComparison.Ordinal);
        Assert.Contains("Quality Gate", work.Instruction, StringComparison.Ordinal);
        Assert.Contains("Go/No-Go", work.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvaBOReviewForaDeOrdemERecusadoComACadeiaDeDependencia()
    {
        // O estado EXATO do despacho real: 03/08 23:48, cards FEAT da fase 5 criados às 23:33 e
        // nenhum concluído.
        var snapshot = ProjectGraphReplayImporter.Import(Archive.Value, DespachoDoReview);
        var projection = ProjectGraphProjector.Project(snapshot, Now);
        var completion = ProjectGraphReplayImporter.CardCompletionAsOf(
            Archive.Value, DespachoDoReview);

        var review = projection.Nodes.Single(node =>
            node.Type == GraphNodeType.Card &&
            node.Title.Contains("Code review", StringComparison.Ordinal));

        // Predecessores do review pela mesma travessia do readiness (depends_on, Accepted).
        var upstream = projection.Edges
            .Where(edge => edge.Status == GraphEdgeStatus.Accepted &&
                edge.RelationType == GraphRelationType.DependsOn &&
                string.Equals(edge.FromNodeId, review.Id, StringComparison.Ordinal))
            .Select(edge => edge.ToNodeId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(upstream);

        var nodesById = projection.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var incomplete = upstream
            .Where(id => !completion.GetValueOrDefault(nodesById[id].CanonicalSourceId))
            .ToArray();
        Assert.Equal(upstream, incomplete);

        var readiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
            "revisao", HasInstruction: true, IsBlocked: false,
            StaleUpstream: [], IncompleteUpstream: incomplete));

        Assert.False(readiness.IsDispatchable);
        foreach (var id in incomplete)
        {
            Assert.Contains(
                $"{CardReadinessEvaluator.UpstreamIncomplete}:{id}", readiness.Blockers);
        }
    }

    private static string NodeIdByTitle(ProjectGraphProjection projection, string title) =>
        projection.Nodes.Single(node =>
            node.Type == GraphNodeType.Card &&
            string.Equals(node.Title, title, StringComparison.Ordinal)).Id;

    /// <summary>Eval 3 — requisito removido: o card que o implementava é atingido, com caminho.</summary>
    [Fact]
    public void Eval3RequisitoRemovidoAtingeOCardQueOImplementava()
    {
        var snapshot = ProjectGraphReplayImporter.Import(Archive.Value);
        var projection = ProjectGraphProjector.Project(snapshot, Now);

        var requirement = projection.Nodes.First(node =>
            node.Type == GraphNodeType.Requirement && node.State == GraphNodeState.Active &&
            projection.Edges.Any(edge =>
                edge.RelationType == GraphRelationType.Implements &&
                string.Equals(edge.ToNodeId, node.Id, StringComparison.Ordinal)));

        var impacted = ImpactAnalysisService.Analyze(
            projection.Nodes, projection.Edges, requirement.Id);

        Assert.Contains(impacted, node =>
            node.Classification == ImpactClassification.Direct &&
            projection.Nodes.Single(n => n.Id == node.NodeId).Type == GraphNodeType.Card);
    }

    /// <summary>
    /// Eval 1 — restrição de plataforma muda (Oracle → SQL Server): tudo que era
    /// constrained_by ela é atingido; o resto do grafo fica intocado.
    /// </summary>
    [Fact]
    public void Eval1MudancaDeConstraintAtingeSomenteQuemElaRestringia()
    {
        var nodes = new[]
        {
            new GraphNode(
                "constraint:oracle19c", "01ARZ3NDEKTSV4RRFFQ69G5FAV", GraphNodeType.Constraint,
                "oracle19c", "org_constraint", 1, GraphNodeState.Active,
                GraphProvenance.Deterministic, 1.0, "Banco corporativo Oracle 19c", Now, Now),
            new GraphNode(
                "card:persistencia", "01ARZ3NDEKTSV4RRFFQ69G5FAV", GraphNodeType.Card,
                "persistencia", "work_task", 1, GraphNodeState.Active,
                GraphProvenance.Deterministic, 1.0, "Camada de persistência", Now, Now),
            new GraphNode(
                "card:telas", "01ARZ3NDEKTSV4RRFFQ69G5FAV", GraphNodeType.Card,
                "telas", "work_task", 1, GraphNodeState.Active,
                GraphProvenance.Deterministic, 1.0, "Telas", Now, Now),
        };
        var edges = new[]
        {
            GraphEdge.Structural(
                "card:persistencia", "constraint:oracle19c", GraphRelationType.ConstrainedBy, Now),
        };

        var impacted = ImpactAnalysisService.Analyze(nodes, edges, "constraint:oracle19c");

        var hit = Assert.Single(impacted);
        Assert.Equal("card:persistencia", hit.NodeId);
        Assert.Equal(ImpactClassification.Direct, hit.Classification);
    }

    /// <summary>O importador é idempotente e determinístico: duas importações, o mesmo grafo.</summary>
    [Fact]
    public void OImportadorEDeterministico()
    {
        var first = ProjectGraphProjector.Project(
            ProjectGraphReplayImporter.Import(Archive.Value), Now);
        var second = ProjectGraphProjector.Project(
            ProjectGraphReplayImporter.Import(Archive.Value), Now);

        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Edges, second.Edges);
        Assert.True(first.Nodes.Count > 60, $"grafo pequeno demais: {first.Nodes.Count} nós.");
    }
}
