using Harness.Modules.Coordination.Application;
using Harness.Modules.Workflows.Product.Graph;
using Harness.SharedKernel.Graph;

namespace Harness.UnitTests.Graph;

/// <summary>
/// Onda 2.2/2.3 — o STALE vira trabalho visível (card de revalidação derivado, idempotente) e o
/// readiness segura, fail-closed e com razão enunciável, o card cujo predecessor está STALE ou
/// incompleto.
/// </summary>
public sealed class GraphReadinessAndRevalidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PredecessorStaleSeguraODespachoComONoExatoNaRazao()
    {
        var readiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
            "agent_task", HasInstruction: true, IsBlocked: false,
            StaleUpstream: ["artifact:plano-de-testes"],
            IncompleteUpstream: ["card:c00"]));

        Assert.False(readiness.IsDispatchable);
        Assert.Contains(
            $"{CardReadinessEvaluator.UpstreamStale}:artifact:plano-de-testes",
            readiness.Blockers);
        Assert.Contains(
            $"{CardReadinessEvaluator.UpstreamIncomplete}:card:c00",
            readiness.Blockers);
    }

    [Fact]
    public void SemFatosDeGrafoOComportamentoEIdenticoAoAnterior()
    {
        var readiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
            "agent_task", HasInstruction: true, IsBlocked: false));

        Assert.True(readiness.IsDispatchable);
        Assert.Empty(readiness.Blockers);
    }

    [Fact]
    public void RevalidacaoEDerivadaComCausaCaminhoEImpressaoDigitalEstavel()
    {
        var cause = new GraphNode(
            "requirement:r1", "01ARZ3NDEKTSV4RRFFQ69G5FAV", GraphNodeType.Requirement,
            "r1", "demand", 2, GraphNodeState.Active, GraphProvenance.Deterministic, 1.0,
            "Fronteira do escopo", Now, Now);
        var impacted = new[]
        {
            new ImpactedNode(
                "artifact:plano-de-testes", ImpactClassification.Direct,
                ["requirement:r1", "artifact:plano-de-testes"]),
            new ImpactedNode(
                "card:review", ImpactClassification.Possible, ["requirement:r1", "card:review"]),
        };
        var nodesById = new Dictionary<string, GraphNode>(StringComparer.Ordinal)
        {
            ["artifact:plano-de-testes"] = cause with
            {
                Id = "artifact:plano-de-testes",
                Type = GraphNodeType.Artifact,
                Title = "Plano de testes",
            },
        };

        var work = GraphRevalidationWorks.From(cause, impacted, nodesById);

        Assert.NotNull(work);
        // Idempotência: a mesma causa+versão produz sempre a mesma impressão digital.
        Assert.Equal(GraphRevalidationWorks.Fingerprint("requirement:r1", 2), work.Fingerprint);
        Assert.StartsWith(
            $"{GraphRevalidationWorks.TitlePrefix} {work.Fingerprint}", work.Title,
            StringComparison.Ordinal);
        Assert.Contains("Plano de testes", work.Instruction, StringComparison.Ordinal);
        Assert.Contains("requirement:r1 → artifact:plano-de-testes", work.Instruction, StringComparison.Ordinal);
        // O que é só Possible NÃO entra no trabalho de revalidação.
        Assert.DoesNotContain("card:review", work.Instruction, StringComparison.Ordinal);
        // Versão nova da mesma causa = problema novo = card novo.
        Assert.NotEqual(work.Fingerprint, GraphRevalidationWorks.Fingerprint("requirement:r1", 3));
    }

    [Fact]
    public void ImpactoApenasPossibleNaoGeraTrabalhoNenhum()
    {
        var cause = new GraphNode(
            "requirement:r1", "01ARZ3NDEKTSV4RRFFQ69G5FAV", GraphNodeType.Requirement,
            "r1", "demand", 2, GraphNodeState.Active, GraphProvenance.Deterministic, 1.0,
            "Req", Now, Now);

        var work = GraphRevalidationWorks.From(
            cause,
            [new ImpactedNode("card:c1", ImpactClassification.Possible, ["requirement:r1", "card:c1"])],
            new Dictionary<string, GraphNode>(StringComparer.Ordinal));

        Assert.Null(work);
    }
}
