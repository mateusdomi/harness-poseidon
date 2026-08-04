using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// O gate que separa "código gerado" de "sistema entregue". Cada cenário aqui corresponde a uma
/// decisão que o Poseidon tomava errado ou não tomava.
/// </summary>
public sealed class ProductDeliveryGateTests
{
    [Fact]
    public void WebProductWithoutFrontendFails()
    {
        var verdict = Evaluate(Web(), Complete().Where(evidence =>
            evidence.Kind is not (ProductEvidenceKind.FrontendPresent
                or ProductEvidenceKind.FrontendBuild
                or ProductEvidenceKind.FrontendBackendIntegration
                or ProductEvidenceKind.E2EJourneyPassed)));

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.FrontendPresent);
        Assert.Contains("frontendpresent", verdict.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteWebProductPasses()
    {
        var verdict = Evaluate(Web(), Complete());

        Assert.True(verdict.Satisfied);
        Assert.Empty(verdict.Findings);
    }

    [Fact]
    public void ApiOnlyProductWithoutFrontendPasses()
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("project", "Quero somente uma API de empréstimos.", []));

        var verdict = Evaluate(profile, Complete().Where(evidence =>
            evidence.Kind is not (ProductEvidenceKind.FrontendPresent
                or ProductEvidenceKind.FrontendBuild
                or ProductEvidenceKind.FrontendBackendIntegration
                or ProductEvidenceKind.E2EJourneyPassed)));

        Assert.True(verdict.Satisfied);
        Assert.DoesNotContain(ProductEvidenceKind.FrontendPresent, verdict.Required);
    }

    [Fact]
    public void HttpApiWithoutOpenApiEvidenceFails()
    {
        var verdict = Evaluate(Web(), Complete().Where(evidence =>
            evidence.Kind != ProductEvidenceKind.OpenApiGenerated));

        Assert.False(verdict.Satisfied);
        var finding = Assert.Single(verdict.Findings);
        Assert.Equal(ProductEvidenceKind.OpenApiGenerated, finding.Kind);
        Assert.Equal(ProductEvidenceGap.Missing, finding.Gap);
    }

    [Fact]
    public void EvidenceReportedAndFailedIsDistinguishedFromEvidenceNeverProduced()
    {
        var verdict = Evaluate(Web(), Complete()
            .Where(evidence => evidence.Kind != ProductEvidenceKind.FrontendBuild)
            .Append(new ProductEvidence(ProductEvidenceKind.FrontendBuild, false, "vite build falhou")));

        var finding = Assert.Single(verdict.Findings);
        Assert.Equal(ProductEvidenceGap.Failed, finding.Gap);
    }

    [Fact]
    public void FrontendThatOnlyRendersMockDoesNotSatisfyTheIntegrationRequirement()
    {
        var verdict = Evaluate(Web(), Complete()
            .Where(evidence => evidence.Kind != ProductEvidenceKind.FrontendBackendIntegration)
            .Append(new ProductEvidence(
                ProductEvidenceKind.FrontendBackendIntegration, false, "tela alimentada por mock")));

        Assert.False(verdict.Satisfied);
    }

    [Fact]
    public void UnresolvedModalityNeverPasses()
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("project", "faça o combinado", []));

        var verdict = Evaluate(profile, Complete());

        Assert.Equal(ProductModality.Unspecified, profile.Modality);
        Assert.False(verdict.Satisfied);
    }

    [Fact]
    public void AngularOverrideDoesNotMakeTheGateDemandReact()
    {
        // O gate exige que a interface EXISTA e funcione; ele não é o lugar de exigir framework.
        var profile = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "project",
            "Sistema de empréstimos.",
            [new ProfileDirective(
                ProfileAuthority.ProjectRequirement,
                EffectiveProfileResolver.AreaFrontendFramework,
                "Angular",
                "Padronização corporativa")]));

        var verdict = Evaluate(profile, Complete());

        Assert.True(verdict.Satisfied);
        Assert.Equal("Angular", profile.Frontend.Framework);
    }

    [Fact]
    public void PhaseGateStaysClosedWhenTheProductVerdictFailsEvenWithEveryDocumentAccepted()
    {
        // A regressão exata: todas as obrigações DOCUMENTAIS da fase aceitas, e mesmo assim o
        // produto não existe. Documento que afirma que está pronto não é evidência de que está.
        var failing = Evaluate(Web(), []);

        var decision = PhaseGatePolicy.Decide(
            ProjectOperationMode.Autonomous,
            "5-Desenvolvimento",
            "gate-desenvolvimento",
            null,
            new PhaseGateEvidence(
                HasGate: true,
                AllRequiredObligationsAccepted: true,
                HasBlockingFinding: false,
                HasOpenBlocker: false,
                RequiredObligationCount: 4,
                ProductDelivery: failing));

        Assert.Equal(PhaseGateDecision.NotReady, decision);
    }

    [Fact]
    public void PhaseWithoutProductDeliveryVerdictKeepsBehavingAsBefore()
    {
        // Fase documental não precisa provar que existe frontend; a compatibilidade importa.
        var decision = PhaseGatePolicy.Decide(
            ProjectOperationMode.Autonomous,
            "3-Arquitetura",
            "gate-arquitetura",
            null,
            new PhaseGateEvidence(true, true, false, false, 3));

        Assert.Equal(PhaseGateDecision.ChiefApproves, decision);
    }

    private static ProductDeliveryVerdict Evaluate(
        ProjectEffectiveProfile profile, IEnumerable<ProductEvidence> evidence) =>
        ProductDeliveryGate.Evaluate(profile, evidence.ToArray());

    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("project", "Quero um sistema de empréstimos.", []));

    private static IEnumerable<ProductEvidence> Complete() =>
        Enum.GetValues<ProductEvidenceKind>().Select(kind => new ProductEvidence(kind, true));
}
