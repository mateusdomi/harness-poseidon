namespace Harness.Modules.Documents.Contracts;

public enum DocumentKind
{
    Prd,
    Spec,
    Design,
    Runbook,
    Note,
    Report,
}

public enum DocumentState
{
    Planned,
    InElaboration,
    InReview,
    AwaitingApproval,
    Approved,
    Outdated,
    Superseded,
    NotApplicable,
}

public enum DocumentAuthorKind
{
    User,
    Chief,
    Agent,
}

public enum DocumentApprovalState
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

public enum DocumentApprovalPriority
{
    Low,
    Medium,
    High,
    Critical,
}

public enum DocumentApprovalDecision
{
    Approved,
    Rejected,
}
