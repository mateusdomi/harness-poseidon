namespace Harness.Modules.Execution.Infrastructure.Git;

public sealed record GitWorktreeDescriptor(
    string AttemptId,
    string BranchName,
    string WorktreePath,
    string HeadCommit);
