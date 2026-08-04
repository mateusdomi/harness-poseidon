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
    Abandoned,
    Cancelled,
}

public enum ReviewDecision
{
    Approved,
    Rejected,
}

/// <summary>
/// Causa tipada de uma reprovação em <see cref="WorkReview"/>. Usada para métricas,
/// agrupamento no board e direcionamento da correção sem depender de parsing do racional.
/// </summary>
public enum ReviewRejectionCause
{
    None,
    ContextMissing,
    AcceptanceNotMet,
    ScopeViolation,
    QualityBar,
    Other,
}
