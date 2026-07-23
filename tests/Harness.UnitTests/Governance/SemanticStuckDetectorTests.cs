using Harness.Modules.Governance.Metrics;

namespace Harness.UnitTests.Governance;

public sealed class SemanticStuckDetectorTests
{
    private static readonly SemanticStuckDetector Detector = new();

    private static StuckAttemptSignal Signal(
        int number, StuckOutcome outcome, string? reason = null, string? hash = null) =>
        new(number, outcome, reason, hash);

    [Fact]
    public void EmptyHistoryIsNotStuck()
    {
        var verdict = Detector.Evaluate([]);
        Assert.False(verdict.IsStuck);
        Assert.Equal(StuckReason.None, verdict.Reason);
    }

    [Fact]
    public void ProgressResetsAndIsNotStuck()
    {
        var verdict = Detector.Evaluate(
        [
            Signal(1, StuckOutcome.Failed, "boom", "h1"),
            Signal(2, StuckOutcome.Failed, "boom", "h1"),
            Signal(3, StuckOutcome.Succeeded),
        ]);
        Assert.False(verdict.IsStuck);
        Assert.Equal(0, verdict.NoProgressStreak);
    }

    [Fact]
    public void ThreeDistinctConsecutiveFailuresAreStuck()
    {
        var verdict = Detector.Evaluate(
        [
            Signal(1, StuckOutcome.Failed, "reason-a", "h1"),
            Signal(2, StuckOutcome.Failed, "reason-b", "h2"),
            Signal(3, StuckOutcome.Failed, "reason-c", "h3"),
        ]);
        Assert.True(verdict.IsStuck);
        Assert.Equal(StuckReason.ConsecutiveFailures, verdict.Reason);
        Assert.Equal(3, verdict.NoProgressStreak);
    }

    [Fact]
    public void RepeatedFailureReasonIsDetectedBelowConsecutiveThreshold()
    {
        var verdict = Detector.Evaluate(
        [
            Signal(1, StuckOutcome.Failed, "same failure", "h1"),
            Signal(2, StuckOutcome.Failed, "same failure", "h2"),
        ]);
        Assert.True(verdict.IsStuck);
        Assert.Equal(StuckReason.RepeatedFailureReason, verdict.Reason);
    }

    [Fact]
    public void RepeatedInstructionWithoutProgressIsDetected()
    {
        // Two trailing attempts reran the same instruction hash; one is still pending, so it is not
        // a failure loop but a re-issue loop.
        var verdict = Detector.Evaluate(
        [
            Signal(1, StuckOutcome.Failed, "distinct-1", "same-hash"),
            Signal(2, StuckOutcome.Pending, null, "same-hash"),
        ]);
        Assert.True(verdict.IsStuck);
        Assert.Equal(StuckReason.RepeatedInstruction, verdict.Reason);
    }

    [Fact]
    public void SingleFailureIsNotStuck()
    {
        var verdict = Detector.Evaluate([Signal(1, StuckOutcome.Failed, "boom", "h1")]);
        Assert.False(verdict.IsStuck);
        Assert.Equal(1, verdict.NoProgressStreak);
    }

    [Fact]
    public void TrailingPendingBreaksFailureStreak()
    {
        var verdict = Detector.Evaluate(
        [
            Signal(1, StuckOutcome.Failed, "a", "h1"),
            Signal(2, StuckOutcome.Failed, "b", "h2"),
            Signal(3, StuckOutcome.Failed, "c", "h3"),
            Signal(4, StuckOutcome.Pending, null, "h4"),
        ]);
        Assert.False(verdict.IsStuck);
        Assert.Equal(0, verdict.NoProgressStreak);
    }

    [Fact]
    public void HonoursConfiguredThresholds()
    {
        var strict = new SemanticStuckDetector(new StuckDetectorOptions
        {
            NoProgressThreshold = 5,
            RepeatThreshold = 4,
        });
        var verdict = strict.Evaluate(
        [
            Signal(1, StuckOutcome.Failed, "distinct-1", "h1"),
            Signal(2, StuckOutcome.Failed, "distinct-2", "h2"),
            Signal(3, StuckOutcome.Failed, "distinct-3", "h3"),
        ]);
        Assert.False(verdict.IsStuck);
    }
}
