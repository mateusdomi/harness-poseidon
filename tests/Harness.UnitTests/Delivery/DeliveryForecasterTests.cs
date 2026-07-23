using Harness.Modules.Delivery.Application;

namespace Harness.UnitTests.Delivery;

public sealed class DeliveryForecasterTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Committed = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SufficientEvidenceProducesDateAndHighConfidence()
    {
        var result = DeliveryForecaster.Forecast(new ForecastInput(
            MilestonesTotal: 4, MilestonesDone: 3, OpenDependencies: 0, PendingValidations: 0,
            AverageVarianceDays: 1.0, CommittedDate: Committed, AsOf: AsOf));

        Assert.True(result.HasSufficientEvidence);
        Assert.NotNull(result.ForecastDate);
        // Data comprometida ajustada pela variação MÉDIA HISTÓRICA (+1 dia) — nunca um buffer inventado.
        Assert.Equal(Committed.AddDays(1), result.ForecastDate);
        Assert.Equal(ForecastConfidence.High, result.Confidence);
        Assert.Contains(result.Basis, b => b.Signal == "committed_date");
        Assert.Contains(result.Basis, b => b.Signal == "variance_history");
    }

    [Fact]
    public void NoMilestonesYieldsNoDateAndLowConfidenceWithExplainingBasis()
    {
        var result = DeliveryForecaster.Forecast(new ForecastInput(
            MilestonesTotal: 0, MilestonesDone: 0, OpenDependencies: 0, PendingValidations: 0,
            AverageVarianceDays: null, CommittedDate: Committed, AsOf: AsOf));

        Assert.False(result.HasSufficientEvidence);
        Assert.Null(result.ForecastDate);
        Assert.Equal(ForecastConfidence.Low, result.Confidence);
        Assert.Contains(result.Basis, b =>
            b.Signal == "insufficient_evidence" && b.Detail.Contains("no plan milestones"));
    }

    [Fact]
    public void NoCommittedDateNeverInventsADate()
    {
        var result = DeliveryForecaster.Forecast(new ForecastInput(
            MilestonesTotal: 5, MilestonesDone: 4, OpenDependencies: 0, PendingValidations: 0,
            AverageVarianceDays: 2.0, CommittedDate: null, AsOf: AsOf));

        Assert.False(result.HasSufficientEvidence);
        Assert.Null(result.ForecastDate);
        Assert.Equal(ForecastConfidence.Low, result.Confidence);
        Assert.Contains(result.Basis, b =>
            b.Signal == "insufficient_evidence" && b.Detail.Contains("no committed date"));
    }

    [Fact]
    public void OpenBlockersReduceConfidence()
    {
        var clean = DeliveryForecaster.Forecast(new ForecastInput(
            4, 3, 0, 0, 1.0, Committed, AsOf));
        var blocked = DeliveryForecaster.Forecast(new ForecastInput(
            4, 3, 3, 2, 1.0, Committed, AsOf));

        Assert.Equal(ForecastConfidence.High, clean.Confidence);
        Assert.True(blocked.ConfidencePercent < clean.ConfidencePercent);
        Assert.NotEqual(ForecastConfidence.High, blocked.Confidence);
        // Ainda produz data (comprometida + variação): bloqueadores baixam confiança, não apagam a âncora.
        Assert.NotNull(blocked.ForecastDate);
    }

    [Fact]
    public void WithoutVarianceHistoryUsesCommittedDateVerbatim()
    {
        var result = DeliveryForecaster.Forecast(new ForecastInput(
            4, 2, 0, 0, AverageVarianceDays: null, CommittedDate: Committed, AsOf: AsOf));

        Assert.True(result.HasSufficientEvidence);
        Assert.Equal(Committed, result.ForecastDate);
        Assert.Contains(result.Basis, b =>
            b.Signal == "variance_history" && b.Detail.Contains("no completed-milestone variance"));
    }

    [Fact]
    public void IsDeterministic()
    {
        var input = new ForecastInput(6, 3, 1, 1, -2.4, Committed, AsOf);
        var first = DeliveryForecaster.Forecast(input);
        var second = DeliveryForecaster.Forecast(input);

        Assert.Equal(first.ForecastDate, second.ForecastDate);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.ConfidencePercent, second.ConfidencePercent);
        Assert.Equal(first.Basis.Count, second.Basis.Count);
        // -2.4 arredonda para -2 dias (away-from-zero), determinístico.
        Assert.Equal(Committed.AddDays(-2), first.ForecastDate);
    }

    [Fact]
    public void RejectsInvalidCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryForecaster.Forecast(
            new ForecastInput(2, 5, 0, 0, null, Committed, AsOf)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryForecaster.Forecast(
            new ForecastInput(2, 1, -1, 0, null, Committed, AsOf)));
    }
}
