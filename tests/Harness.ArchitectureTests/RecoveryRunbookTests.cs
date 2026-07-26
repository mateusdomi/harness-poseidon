namespace Harness.ArchitectureTests;

public sealed class RecoveryRunbookTests
{
    [Fact]
    public void CanonicalRunbookDefinesDrTargetsBackupsAndExactContinuationProof()
    {
        var runbook = ReadRepositoryFile("docs/backend/runbooks/recovery.md");

        Assert.Contains("RPO de 24 horas", runbook, StringComparison.Ordinal);
        Assert.Contains("RTO de 60 minutos", runbook, StringComparison.Ordinal);
        Assert.Contains("RPO de 5 minutos", runbook, StringComparison.Ordinal);
        Assert.Contains("RTO de 30 minutos", runbook, StringComparison.Ordinal);
        Assert.Contains("`.backup`", runbook, StringComparison.Ordinal);
        Assert.Contains("PITR do PostgreSQL", runbook, StringComparison.Ordinal);
        Assert.Contains("fencing token estritamente maior", runbook, StringComparison.Ordinal);
        Assert.Contains("`step-3`", runbook, StringComparison.Ordinal);
        Assert.Contains("`step-4`", runbook, StringComparison.Ordinal);
        Assert.Contains(
            "ProductionDurableExecutionRecoveryTests",
            runbook,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
        return File.ReadAllText(Path.Combine(root, relativePath));
    }
}
