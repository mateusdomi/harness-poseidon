using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Coordination.Domain.Work;

namespace Harness.UnitTests.Coordination;

public sealed class WorkReviewCyclePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RejectionWithinLimitReturnsTaskToRunning(int completedReviewCycles)
    {
        var policy = new WorkReviewCyclePolicy(maximumReviewCycles: 3);

        var decision = policy.EvaluateRejectedReview(completedReviewCycles);

        Assert.Equal(WorkTaskState.Running, decision.TargetState);
        Assert.Equal(WorkTaskTransitionEvent.ReviewRejected, decision.TransitionEvent);
        Assert.True(
            WorkTaskTransitionPolicy.IsAllowed(
                WorkTaskState.Review,
                decision.TargetState,
                decision.TransitionEvent));
    }

    [Fact]
    public void RejectionBeyondLimitEscalatesTask()
    {
        var policy = new WorkReviewCyclePolicy(maximumReviewCycles: 3);

        var decision = policy.EvaluateRejectedReview(completedReviewCycles: 4);

        Assert.Equal(WorkTaskState.Escalated, decision.TargetState);
        Assert.Equal(WorkTaskTransitionEvent.ReviewLimitExceeded, decision.TransitionEvent);
        Assert.True(
            WorkTaskTransitionPolicy.IsAllowed(
                WorkTaskState.Review,
                decision.TargetState,
                decision.TransitionEvent));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaximumReviewCyclesMustBePositive(int maximumReviewCycles)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkReviewCyclePolicy(maximumReviewCycles));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CompletedReviewCyclesMustBePositive(int completedReviewCycles)
    {
        var policy = new WorkReviewCyclePolicy(maximumReviewCycles: 3);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.EvaluateRejectedReview(completedReviewCycles));
    }
}
