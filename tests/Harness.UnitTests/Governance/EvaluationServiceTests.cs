using Harness.Modules.Governance.Evaluation;

namespace Harness.UnitTests.Governance;

public sealed class EvaluationServiceTests
{
    [Fact]
    public void CalculateCompositeScoreReturnsOneForPerfectRun()
    {
        var service = new EvaluationService();
        var score = service.CalculateCompositeScore(
            testPassRate: 1.0,
            findingCountP0: 0,
            findingCountP1: 0,
            findingCountP2: 0,
            findingCountP3: 0,
            overallPass: true);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void CalculateCompositeScoreAppliesPenaltiesForFindings()
    {
        var service = new EvaluationService();
        var score = service.CalculateCompositeScore(
            testPassRate: 1.0,
            findingCountP0: 1, // -0.40
            findingCountP1: 1, // -0.20
            findingCountP2: 0,
            findingCountP3: 0,
            overallPass: true); // base 1.0 -> 1.0 - 0.60 = 0.40

        Assert.Equal(0.40, score);
    }

    [Fact]
    public void CalculateConfidenceIntervalReturnsZeroBoundsForEmptySample()
    {
        var service = new EvaluationService();
        var (lower, upper) = service.CalculateConfidenceInterval(sampleSize: 0, successCount: 0);

        Assert.Equal(0.0, lower);
        Assert.Equal(0.0, upper);
    }

    [Fact]
    public void CalculateConfidenceIntervalCalculatesWilsonScoreBounds()
    {
        var service = new EvaluationService();
        var (lower, upper) = service.CalculateConfidenceInterval(sampleSize: 10, successCount: 10);

        Assert.True(lower > 0.65);
        Assert.Equal(1.0, upper);
        Assert.True(lower <= upper);
    }

    [Fact]
    public void AggregatePerformanceGroupsByProviderAndModel()
    {
        var service = new EvaluationService();
        var now = DateTimeOffset.UtcNow;

        var results = new List<FreshContextEvaluationResult>
        {
            new("v1", "eval-1", EvaluationVerdict.Pass, [], "anthropic", "claude-3-7-sonnet", true, true, now),
            new("v1", "eval-2", EvaluationVerdict.Pass, [], "anthropic", "claude-3-7-sonnet", true, true, now),
            new("v1", "eval-3", EvaluationVerdict.Fail, [new EvaluationFinding(ReviewPriority.P0, 0.9m, "fail", "path", "1", "rule-1", "fix")], "openai", "codex", true, true, now)
        };

        var aggregates = service.AggregatePerformance(results, minSampleSize: 2);

        Assert.Equal(2, aggregates.Count);

        var anthropicAgg = aggregates.First(a => a.Provider == "anthropic");
        Assert.Equal(2, anthropicAgg.SampleSize);
        Assert.Equal(1.0, anthropicAgg.PassRate);
        Assert.True(anthropicAgg.SampleSizeQualified);

        var openaiAgg = aggregates.First(a => a.Provider == "openai");
        Assert.Equal(1, openaiAgg.SampleSize);
        Assert.Equal(0.0, openaiAgg.PassRate);
        Assert.False(openaiAgg.SampleSizeQualified);
    }

    [Fact]
    public void GenerateRecommendationsFlagsInsufficientSampleSizeWhenBelowMinThreshold()
    {
        var service = new EvaluationService();
        var now = DateTimeOffset.UtcNow;

        var aggregates = new List<PerformanceAggregate>
        {
            new("anthropic", "claude-3-7-sonnet", "anthropic", null, 3, 3, 0, 1.0, 1.0, 0.70, 1.0, SampleSizeQualified: false)
        };

        var recs = service.GenerateRecommendations(aggregates, now);

        Assert.Single(recs);
        Assert.Equal("insufficient_sample_size", recs[0].Action);
    }

    [Fact]
    public void GenerateRecommendationsRecommendsHighPerformanceWhenQualifiedAndScoreHigh()
    {
        var service = new EvaluationService();
        var now = DateTimeOffset.UtcNow;

        var aggregates = new List<PerformanceAggregate>
        {
            new("anthropic", "claude-3-7-sonnet", "anthropic", null, 20, 20, 0, 1.0, 0.95, 0.86, 1.0, SampleSizeQualified: true)
        };

        var recs = service.GenerateRecommendations(aggregates, now);

        Assert.Single(recs);
        Assert.Equal("recommend_high_performance", recs[0].Action);
    }
}
