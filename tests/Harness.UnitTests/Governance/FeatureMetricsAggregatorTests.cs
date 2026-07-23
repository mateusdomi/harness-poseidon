using Harness.Modules.Governance.Metrics;

namespace Harness.UnitTests.Governance;

public sealed class FeatureMetricsAggregatorTests
{
    [Theory]
    [InlineData("[CAT-04] Wire the evaluator", "CAT-04")]
    [InlineData("CAT-04/T03 Implement store", "CAT-04")]
    [InlineData("PLAT-04: Evals + metrics", "PLAT-04")]
    [InlineData("PLAT-04 - build the layer", "PLAT-04")]
    [InlineData("plat-04 lowercase title", "unassigned")]
    [InlineData("No feature token here", "unassigned")]
    [InlineData("", "unassigned")]
    public void ParsesFeatureIdFromLeadingToken(string title, string expected)
    {
        Assert.Equal(expected, FeatureIdParser.Parse(title));
    }

    [Fact]
    public void AggregatesOutcomesCostAndTokensPerFeatureFromRecordedAttempts()
    {
        var attempts = new[]
        {
            // CAT-04, task A: one approved success + one rejected failure.
            new FeatureAttemptInput("A", "[CAT-04] task A", "approved", "completed", 0.10m, 100, 40, 1_000),
            new FeatureAttemptInput("A", "[CAT-04] task A", "rejected", "failed", 0.05m, 50, 10, 500),
            // CAT-04, task B: an in-progress attempt (running) — neither success nor failure.
            new FeatureAttemptInput("B", "CAT-04/T02 task B", "running", "running", 0.02m, 20, 0, null),
            // PLAT-04, task C: operational cancelled counts as failure even though review state differs.
            new FeatureAttemptInput("C", "PLAT-04 task C", "awaiting_review", "cancelled", 0.03m, 30, 5, 300),
        };

        var snapshot = FeatureMetricsAggregator.Aggregate("proj", attempts);

        Assert.Equal("proj", snapshot.ProjectId);
        Assert.Equal(2, snapshot.Features.Count);

        var cat = snapshot.Features.Single(feature => feature.FeatureId == "CAT-04");
        Assert.Equal(2, cat.TaskCount);
        Assert.Equal(3, cat.AttemptCount);
        Assert.Equal(1, cat.SuccessCount);
        Assert.Equal(1, cat.FailureCount);
        Assert.Equal(1, cat.InProgressCount);
        Assert.Equal(0.17m, cat.TotalCostUsd);
        Assert.Equal(170, cat.TotalTokensInput);
        Assert.Equal(50, cat.TotalTokensOutput);
        Assert.Equal(1_500, cat.TotalDurationMs);

        var plat = snapshot.Features.Single(feature => feature.FeatureId == "PLAT-04");
        Assert.Equal(1, plat.AttemptCount);
        Assert.Equal(0, plat.SuccessCount);
        Assert.Equal(1, plat.FailureCount);
    }

    [Fact]
    public void GroupsUnparseableTitlesUnderUnassignedBucket()
    {
        var attempts = new[]
        {
            new FeatureAttemptInput("A", "loose task", "approved", "completed", 0m, 0, 0, null),
        };

        var snapshot = FeatureMetricsAggregator.Aggregate("proj", attempts);

        Assert.Equal("unassigned", Assert.Single(snapshot.Features).FeatureId);
    }

    [Fact]
    public void EmptyAttemptsProduceEmptySnapshot()
    {
        Assert.Empty(FeatureMetricsAggregator.Aggregate("proj", []).Features);
    }
}
