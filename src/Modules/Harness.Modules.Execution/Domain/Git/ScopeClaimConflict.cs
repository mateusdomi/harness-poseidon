namespace Harness.Modules.Execution.Domain.Git;

public sealed record ScopeClaimConflict(
    string ExistingAttemptId,
    ScopeClaim RequestedClaim,
    ScopeClaim ExistingClaim);
