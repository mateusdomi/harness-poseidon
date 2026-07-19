namespace Harness.Persistence.Abstractions.AttemptWorkspaces;

public sealed record AttemptWorkspaceLifecycleEventPayload(
    string ProjectId,
    string TaskId,
    string AttemptId,
    string State,
    string CleanupState,
    string BranchName,
    string WorktreePath,
    IReadOnlyList<string> ScopeClaims,
    string Owner,
    long FencingToken,
    string? TechnicalExecutionId,
    string? CommitSha,
    string? SessionId,
    string? FinalError);
