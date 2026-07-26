namespace Harness.Modules.Coordination.Contracts;

public enum WorkRiskTier
{
    Low,
    Medium,
    High,
    Critical,
}

public enum WorkTaskState
{
    Draft,
    Triaged,
    Ready,
    Assigned,
    Running,
    Review,
    Blocked,
    Approved,
    Escalated,
    Merged,
    Done,
    Cancelled,
    AwaitingReview = Review,
    Completed = Done,
}

public enum WorkTaskTransitionEvent
{
    Triaged,
    RequirementsCompleted,
    LeaseAcquired,
    HeartbeatConfirmed,
    AttemptSubmitted,
    Blocked,
    LeaseExpired,
    Unblocked,
    ReviewApproved,
    ReviewRejected,
    ReviewLimitExceeded,
    Replanned,
    MergeCompleted,
    DeliveryCompleted,
    Cancelled,
}

public enum WorkAttemptState
{
    Running,
    AwaitingReview,
    Approved,
    Rejected,
}

public enum ReviewDecision
{
    Approved,
    Rejected,
}
