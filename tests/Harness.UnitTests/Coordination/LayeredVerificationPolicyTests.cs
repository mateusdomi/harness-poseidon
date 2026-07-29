using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class LayeredVerificationPolicyTests
{
    private static LayerResult Pass(VerificationLayer layer) => new(layer, LayerVerdict.Pass, "ok");

    private static LayerResult Fail(VerificationLayer layer) => new(layer, LayerVerdict.Fail, "nao");

    /// <summary>Gate da fase: veredito de aprovação com as três camadas verdes.</summary>
    [Fact]
    public void AllThreeLayersGreenApproves()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            Pass(VerificationLayer.Deterministic),
            Pass(VerificationLayer.Behavioral),
            Pass(VerificationLayer.Intent)
        ]);

        Assert.True(outcome.Approved);
        Assert.Null(outcome.BlockedAt);
        Assert.Equal("none", outcome.Severity);
    }

    /// <summary>Gate da fase: camada superior NÃO compensa inferior.</summary>
    [Fact]
    public void AnEnthusiasticIntentEvaluationDoesNotTurnARedTestGreen()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            Fail(VerificationLayer.Deterministic),
            Pass(VerificationLayer.Behavioral),
            Pass(VerificationLayer.Intent)
        ]);

        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Deterministic, outcome.BlockedAt);
        Assert.Equal(LayeredVerificationPolicy.ReasonDeterministicFailed, outcome.ReasonCode);
        Assert.Equal("blocker", outcome.Severity);
    }

    /// <summary>Gate da fase: compilar e testar não prova que serve para o que foi pedido.</summary>
    [Fact]
    public void PassingTheCheapLayerDoesNotExcuseFailingIntent()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            Pass(VerificationLayer.Deterministic),
            Pass(VerificationLayer.Behavioral),
            Fail(VerificationLayer.Intent)
        ]);

        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Intent, outcome.BlockedAt);
        Assert.Equal("major", outcome.Severity);
    }

    [Fact]
    public void BehavioralFailureBlocksBeforeSpendingTheIntentEvaluator()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            Pass(VerificationLayer.Deterministic),
            Fail(VerificationLayer.Behavioral),
            Pass(VerificationLayer.Intent)
        ]);

        Assert.Equal(VerificationLayer.Behavioral, outcome.BlockedAt);
        Assert.Equal(LayeredVerificationPolicy.ReasonBehavioralFailed, outcome.ReasonCode);
    }

    /// <summary>Camada não executada é bloqueio: "ninguém olhou" não é "está bom".</summary>
    [Theory]
    [InlineData(VerificationLayer.Deterministic)]
    [InlineData(VerificationLayer.Behavioral)]
    [InlineData(VerificationLayer.Intent)]
    public void AMissingLayerBlocksInsteadOfPassingSilently(VerificationLayer missing)
    {
        var results = LayeredVerificationPolicy.Order
            .Where(layer => layer != missing)
            .Select(Pass)
            .ToArray();

        var outcome = LayeredVerificationPolicy.Evaluate(results);

        Assert.False(outcome.Approved);
        Assert.Equal(missing, outcome.BlockedAt);
        Assert.Equal(LayeredVerificationPolicy.ReasonLayerNotRun, outcome.ReasonCode);
    }

    [Fact]
    public void AnExplicitNotRunIsTreatedTheSameAsAbsent()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            Pass(VerificationLayer.Deterministic),
            new LayerResult(VerificationLayer.Behavioral, LayerVerdict.NotRun, "pulou"),
            Pass(VerificationLayer.Intent)
        ]);

        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Behavioral, outcome.BlockedAt);
    }

    [Fact]
    public void NoResultAtAllBlocksAtTheCheapestLayer()
    {
        var outcome = LayeredVerificationPolicy.Evaluate([]);

        Assert.False(outcome.Approved);
        Assert.Equal(VerificationLayer.Deterministic, outcome.BlockedAt);
    }

    /// <summary>O revisor caro só é ocupado depois que a camada barata passou.</summary>
    [Fact]
    public void TheReviewerIsOnlyOccupiedAfterTheDeterministicLayerPasses()
    {
        Assert.False(LayeredVerificationPolicy.MayOccupyReviewer([]));
        Assert.False(LayeredVerificationPolicy.MayOccupyReviewer([Fail(VerificationLayer.Deterministic)]));
        Assert.False(LayeredVerificationPolicy.MayOccupyReviewer(
            [new LayerResult(VerificationLayer.Deterministic, LayerVerdict.NotRun, "x")]));
        Assert.True(LayeredVerificationPolicy.MayOccupyReviewer([Pass(VerificationLayer.Deterministic)]));
    }

    [Fact]
    public void ARerunOfTheSameLayerReplacesThePreviousVerdict()
    {
        var outcome = LayeredVerificationPolicy.Evaluate(
        [
            Fail(VerificationLayer.Deterministic),
            Pass(VerificationLayer.Deterministic),
            Pass(VerificationLayer.Behavioral),
            Pass(VerificationLayer.Intent)
        ]);

        Assert.True(outcome.Approved);
    }

    [Fact]
    public void TheOrderGoesFromCheapestToMostExpensive()
    {
        Assert.Equal(
            [VerificationLayer.Deterministic, VerificationLayer.Behavioral, VerificationLayer.Intent],
            LayeredVerificationPolicy.Order);
    }
}
