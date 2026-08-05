using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// Graph Phase 0: o read model que junta as derivações existentes numa fotografia por projeto.
/// A prova central é a do run de empréstimos, recontada: com o snapshot, a fatia órfã de interface
/// e a parede repetida ficariam visíveis lado a lado — que é o que ninguém via em 04/08.
/// </summary>
public sealed class ProjectSituationSnapshotTests
{
    [Fact]
    public void OCenarioDeEmprestimosFicaVisivelNumaFotografia()
    {
        var coverage = RequirementCoverageAnalyzer.Analyze(
            [("req-interface", "pessoa consegue operar o sistema", false)],
            new Dictionary<string, IReadOnlyList<RequirementCard>>(StringComparer.Ordinal)
            {
                ["req-interface"] =
                [
                    new RequirementCard("t02a", "FEAT/T02", "cancelled", false),
                    new RequirementCard("t02b", "FEAT/T02", "cancelled", false),
                ],
            },
            deliverySatisfied: false);

        var verdict = new ProductDeliveryVerdict(
            false, ProductModality.Web, [],
            [new ProductEvidenceFinding(
                ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed, "sem interface")]);
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs(
                "emprestimos", "Sistema web de empréstimos usado pela equipe.", []));

        var snapshot = ProjectSituationComposer.Compose(
            "01PROJ", "Empréstimos",
            new ProjectAttentionFacts(false, 0, false, 0, 0, false, false),
            coverage,
            [("t01", true, false)],
            ProductGapCorrections.From(verdict, profile),
            NoProgressGuard.Evaluate([
                [new ProductEvidenceFinding(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed, "x")],
                [new ProductEvidenceFinding(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed, "y")],
                [new ProductEvidenceFinding(ProductEvidenceKind.FrontendPresent, ProductEvidenceGap.Failed, "z")],
            ]));

        // A fotografia junta o que estava espalhado: requisito órfão, correção derivada e parede.
        Assert.Single(snapshot.UncoveredRequirements);
        Assert.Equal("req-interface", snapshot.UncoveredRequirements[0].RequirementId);
        Assert.Single(snapshot.CorrectiveWork);
        Assert.Contains("não tem interface", snapshot.CorrectiveWork[0], StringComparison.Ordinal);
        Assert.Single(snapshot.RepeatedFailures);
        Assert.Equal(["t01"], snapshot.RunnableFrontier);
        Assert.False(snapshot.Idle);
    }

    [Fact]
    public void ProjetoSemNadaExecutavelESemCorrecaoEstaIdle()
    {
        var snapshot = ProjectSituationComposer.Compose(
            "01PROJ", "Quieto",
            new ProjectAttentionFacts(false, 0, false, 0, 0, false, false),
            [], [], [],
            NoProgressGuard.Evaluate([]));

        Assert.True(snapshot.Idle);
        Assert.Equal(ProjectAttentionState.AutonomouslyProgressing, snapshot.Attention);
    }
}
