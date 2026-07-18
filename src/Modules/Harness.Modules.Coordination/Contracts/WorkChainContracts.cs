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
    Ready,
    Running,
    AwaitingReview,
    Completed,
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
