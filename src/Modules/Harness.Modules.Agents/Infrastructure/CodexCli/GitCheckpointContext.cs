namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public sealed record GitCheckpointContext(
    string RepositoryPath,
    string CommitSha,
    bool WorkingTreeClean)
{
    public string CreateDeveloperInstructions() =>
        $"""
        Rehydrate this coding session from persisted sources of truth.
        Repository: {RepositoryPath}
        Exact Git checkpoint: {CommitSha}
        Working tree clean at capture: {WorkingTreeClean}
        Reconstruct context from the checkpoint, persisted task instruction and catalogued artifact hashes.
        Do not depend on a previous provider session being available.
        """;
}
