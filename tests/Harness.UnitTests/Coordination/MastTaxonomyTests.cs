using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class MastTaxonomyTests
{
    /// <summary>Gate da fase: os 14 modos existem, em 3 categorias.</summary>
    [Fact]
    public void TheTaxonomyHasExactlyFourteenModesInThreeCategories()
    {
        Assert.Equal(MastTaxonomy.ExpectedModeCount, MastTaxonomy.All.Count);
        Assert.Equal(3, MastTaxonomy.All.Select(mode => mode.Category).Distinct().Count());
        Assert.Equal(5, MastTaxonomy.ByCategory(MastCategory.SpecificationAndDesign).Count);
        Assert.Equal(6, MastTaxonomy.ByCategory(MastCategory.InterAgentMisalignment).Count);
        Assert.Equal(3, MastTaxonomy.ByCategory(MastCategory.VerificationAndTermination).Count);
    }

    [Fact]
    public void EveryModeIsClassifiableAndCarriesACorrectiveLever()
    {
        Assert.All(MastTaxonomy.All, mode =>
        {
            Assert.Same(mode, MastTaxonomy.Find(mode.Code));
            // Um modo sem alavanca de correção é rótulo, não diagnóstico.
            Assert.False(string.IsNullOrWhiteSpace(mode.CorrectiveLever));
            Assert.False(string.IsNullOrWhiteSpace(mode.BusinessDescription));
        });
    }

    [Fact]
    public void ModeCodesAreUnique()
    {
        Assert.Equal(
            MastTaxonomy.ExpectedModeCount,
            MastTaxonomy.All.Select(mode => mode.Code).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AnUnknownCodeIsNotInvented()
    {
        Assert.Null(MastTaxonomy.Find("mast.9_9_inexistente"));
        Assert.Null(MastTaxonomy.Find(null));
        Assert.Null(MastTaxonomy.Find("  "));
    }

    /// <summary>
    /// Gate da fase: a distribuição orienta a decisão — e decompor mais, a reação instintiva, está
    /// errada em dois dos três casos.
    /// </summary>
    [Fact]
    public void SpecificationFailuresRecommendRewritingNotSplittingFurther()
    {
        var distribution = MastDistribution.From(
        [
            MastTaxonomy.DisobeyTaskSpecification,
            MastTaxonomy.UnawareOfTerminationConditions,
            MastTaxonomy.DisobeyTaskSpecification
        ]);

        Assert.Equal(MastCategory.SpecificationAndDesign, distribution.Dominant());
        Assert.Equal(MastAdvice.RewriteSpecification, MastAdvice.Recommend(distribution));
    }

    [Fact]
    public void MisalignmentRecommendsFewerParallelFrontsNotMore()
    {
        var distribution = MastDistribution.From(
        [
            MastTaxonomy.TaskDerailment,
            MastTaxonomy.IgnoredOtherAgentInput,
            MastTaxonomy.InformationWithholding
        ]);

        Assert.Equal(MastAdvice.ReduceParallelism, MastAdvice.Recommend(distribution));
    }

    [Fact]
    public void VerificationFailuresRecommendDeeperReview()
    {
        var distribution = MastDistribution.From(
        [
            MastTaxonomy.PrematureTermination,
            MastTaxonomy.IncorrectVerification
        ]);

        Assert.Equal(MastAdvice.DeepenVerification, MastAdvice.Recommend(distribution));
    }

    [Fact]
    public void ATieHasNoDominantCategoryAndThereforeNoAdvice()
    {
        var distribution = MastDistribution.From(
        [
            MastTaxonomy.DisobeyTaskSpecification,
            MastTaxonomy.TaskDerailment
        ]);

        // Escolher um dos dois por desempate arbitrário faria a Bruna agir com convicção sobre uma
        // leitura que os dados não sustentam.
        Assert.Null(distribution.Dominant());
        Assert.Equal(MastAdvice.NoSignal, MastAdvice.Recommend(distribution));
    }

    [Fact]
    public void NoFailuresMeansNoSignal()
    {
        var distribution = MastDistribution.From([]);

        Assert.Equal(0, distribution.Total);
        Assert.Null(distribution.Dominant());
        Assert.Equal(MastAdvice.NoSignal, MastAdvice.Recommend(distribution));
    }

    [Fact]
    public void UnknownCodesDoNotPolluteTheDistribution()
    {
        var distribution = MastDistribution.From(
            [MastTaxonomy.TaskDerailment, "codigo.invalido", ""]);

        Assert.Equal(1, distribution.Total);
        Assert.Equal(MastCategory.InterAgentMisalignment, distribution.Dominant());
    }

    /// <summary>Cada um dos 14 modos classifica numa categoria e produz conselho coerente.</summary>
    [Fact]
    public void EveryModeAloneProducesTheAdviceOfItsCategory()
    {
        foreach (var mode in MastTaxonomy.All)
        {
            var advice = MastAdvice.Recommend(MastDistribution.From([mode.Code]));
            var expected = mode.Category switch
            {
                MastCategory.SpecificationAndDesign => MastAdvice.RewriteSpecification,
                MastCategory.InterAgentMisalignment => MastAdvice.ReduceParallelism,
                _ => MastAdvice.DeepenVerification
            };
            Assert.Equal(expected, advice);
        }
    }
}
