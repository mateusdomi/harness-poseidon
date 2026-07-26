using Harness.Modules.Coordination.Contracts;

namespace Harness.Modules.Coordination.Domain.Work;

public sealed class WorkReviewCyclePolicy
{
    public WorkReviewCyclePolicy(int maximumReviewCycles)
    {
        if (maximumReviewCycles <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumReviewCycles),
                "Maximum review cycles must be positive.");
        }

        MaximumReviewCycles = maximumReviewCycles;
    }

    public int MaximumReviewCycles { get; }

    public WorkReviewCycleDecision EvaluateRejectedReview(int completedReviewCycles)
    {
        if (completedReviewCycles <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedReviewCycles),
                "Completed review cycles must be positive.");
        }

        return completedReviewCycles <= MaximumReviewCycles
            ? new WorkReviewCycleDecision(
                WorkTaskState.Running,
                WorkTaskTransitionEvent.ReviewRejected)
            : new WorkReviewCycleDecision(
                WorkTaskState.Escalated,
                WorkTaskTransitionEvent.ReviewLimitExceeded);
    }
}

public readonly record struct WorkReviewCycleDecision(
    WorkTaskState TargetState,
    WorkTaskTransitionEvent TransitionEvent);
