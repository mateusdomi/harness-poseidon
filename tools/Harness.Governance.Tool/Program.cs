using Harness.Modules.Governance.Documentation;

var command = args.Length > 0 ? args[0] : "lint";
var repositoryRoot = args.Length > 1
    ? Path.GetFullPath(args[1])
    : FindRepositoryRoot(Environment.CurrentDirectory);

try
{
    return command switch
    {
        "lint" => RunLint(repositoryRoot, args.Contains("--warnings-as-errors", StringComparer.Ordinal)),
        "generate" => RunGenerate(repositoryRoot),
        "check-generated" => RunGeneratedCheck(repositoryRoot),
        "sync" => RunSync(repositoryRoot),
        _ => Usage(command)
    };
}
catch (Exception exception) when (exception is GovernanceManifestException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"governance-tool: {exception.Message}");
    return 1;
}

static int RunLint(string root, bool warningsAsErrors)
{
    var report = new GovernanceDocumentLinter(root).Lint(DateTimeOffset.UtcNow);
    foreach (var finding in report.Findings)
    {
        var writer = finding.Severity == GovernanceFindingSeverity.Error ? Console.Error : Console.Out;
        writer.WriteLine($"{finding.Severity.ToString().ToUpperInvariant()} {finding.Code} {finding.Path ?? "-"}: {finding.Message}");
    }

    Console.WriteLine($"governance-lint: errors={report.ErrorCount} warnings={report.WarningCount}");
    return report.ErrorCount > 0 || (warningsAsErrors && report.WarningCount > 0) ? 1 : 0;
}

static int RunGenerate(string root)
{
    var files = new GovernanceDocumentGenerator(root).Generate();
    Console.WriteLine($"governance-generate: wrote {string.Join(", ", files)}");
    return 0;
}

static int RunGeneratedCheck(string root)
{
    var findings = new GovernanceDocumentGenerator(root).Check();
    foreach (var finding in findings)
    {
        Console.Error.WriteLine($"ERROR {finding.Code} {finding.Path}: {finding.Message}");
    }

    Console.WriteLine($"governance-generated-check: errors={findings.Count}");
    return findings.Count == 0 ? 0 : 1;
}

static int RunSync(string root)
{
    var changed = new GovernanceManifestSynchronizer(root).Synchronize(DateTimeOffset.UtcNow);
    Console.WriteLine($"governance-sync: computed fields changed={changed}");
    return 0;
}

static int Usage(string command)
{
    Console.Error.WriteLine($"Unknown governance command '{command}'. Use lint, generate, check-generated or sync.");
    return 2;
}

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")) &&
            Directory.Exists(Path.Combine(directory.FullName, "governance")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the Poseidon repository root.");
}
