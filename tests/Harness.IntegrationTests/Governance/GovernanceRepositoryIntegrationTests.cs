using Harness.Modules.Governance.Documentation;

namespace Harness.IntegrationTests.Governance;

public sealed class GovernanceRepositoryIntegrationTests
{
    [Fact]
    public void RepositoryManifestLinksChecksumsAndGeneratedAdaptersAreConsistent()
    {
        var repositoryRoot = FindRepositoryRoot();

        var report = new GovernanceDocumentLinter(repositoryRoot)
            .Lint(DateTimeOffset.UtcNow);

        Assert.True(
            report.IsValid && report.WarningCount == 0,
            string.Join(Environment.NewLine, report.Findings.Select(finding => $"{finding.Code} {finding.Path}: {finding.Message}")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
