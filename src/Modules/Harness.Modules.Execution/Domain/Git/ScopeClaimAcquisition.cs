namespace Harness.Modules.Execution.Domain.Git;

public sealed record ScopeClaimAcquisition(
    bool Acquired,
    IReadOnlyList<ScopeClaimConflict> Conflicts);
