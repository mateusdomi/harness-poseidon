using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

public sealed class QualityCatalogTests
{
    [Fact]
    public void CatalogoRepresentaAs244PerguntasSemInjetarUmaPerguntaPorItem()
    {
        var summary = QualityCatalog.Summary();

        Assert.Equal(QualityCatalog.SourceChecklistItemCount, summary.TotalSourceItems);
        Assert.True(QualityCatalog.Checks.Count < 40);
        Assert.True(summary.Deterministic > 0);
        Assert.True(summary.Browser > 0);
        Assert.True(summary.LlmJudgement > 0);
        Assert.True(summary.HumanAcceptance > 0);
    }

    [Fact]
    public void BackendOnlyNaoRecebeItensExclusivamenteFrontend()
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("api", "Crie somente uma API backend para empréstimos.", []));

        var selected = QualityCatalog.Select(new QualitySelection(
            profile,
            QualityExecutionMoment.ObjectiveSelfAudit,
            new HashSet<QualitySurface> { QualitySurface.Backend, QualitySurface.Api }));

        Assert.DoesNotContain(selected, check => check.Id is "responsive-layout" or "branding" or "interactive-actions");
        Assert.Contains(selected, check => check.Surfaces.Contains(QualitySurface.Backend));
        Assert.Contains(selected, check => check.Id == "code-quality");
    }

    [Fact]
    public void FrontendIntegradoSelecionaBrowserNetworkEResponsividade()
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("web", "Crie um sistema web integrado com frontend, API e banco.", []));

        var selected = QualityCatalog.Select(new QualitySelection(
            profile,
            QualityExecutionMoment.ObjectiveProof,
            new HashSet<QualitySurface> { QualitySurface.Frontend, QualitySurface.Api, QualitySurface.Integration }));

        Assert.Contains(selected, check => check.Id == "browser-console-network");
        Assert.Contains(selected, check => check.Id == "front-back-integration");
        Assert.Contains(selected, check => check.Id == "responsive-layout");
        Assert.Contains(selected, check => check.ProofType == QualityProofType.Browser);
    }

    [Fact]
    public void TextoDoExecutorNaoSubstituiProvaAutomatizavel()
    {
        var checks = QualityCatalog.Checks
            .Where(check => check.Id is "automated-tests" or "code-quality")
            .ToArray();

        var verdict = QualitySelfAuditGate.Evaluate(
            checks,
            [
                new SelfAuditEvidence("automated-tests", true, ProductEvidenceProvenance.Declared, "actor-text"),
                new SelfAuditEvidence("code-quality", true, ProductEvidenceProvenance.Verified, "lint"),
            ]);

        Assert.False(verdict.Satisfied);
        var finding = Assert.Single(verdict.Findings);
        Assert.Equal("automated-tests", finding.CheckId);
        Assert.Contains("texto do executor não substitui", finding.Reason, StringComparison.Ordinal);
    }
}
