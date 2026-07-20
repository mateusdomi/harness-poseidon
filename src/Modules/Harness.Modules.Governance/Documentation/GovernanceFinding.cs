namespace Harness.Modules.Governance.Documentation;

public enum GovernanceFindingSeverity
{
    Warning,
    Error
}

public sealed record GovernanceFinding(
    GovernanceFindingSeverity Severity,
    string Code,
    string Message,
    string? Path = null);

public sealed class GovernanceLintReport
{
    public GovernanceLintReport(IReadOnlyList<GovernanceFinding> findings)
    {
        Findings = findings;
    }

    public IReadOnlyList<GovernanceFinding> Findings { get; }

    public int ErrorCount => Findings.Count(finding => finding.Severity == GovernanceFindingSeverity.Error);

    public int WarningCount => Findings.Count(finding => finding.Severity == GovernanceFindingSeverity.Warning);

    public bool IsValid => ErrorCount == 0;
}

public sealed class GovernanceManifestException : Exception
{
    public GovernanceManifestException(string message)
        : base(message)
    {
    }

    public GovernanceManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
