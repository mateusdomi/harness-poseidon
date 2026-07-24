using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Harness.Modules.Governance.Documentation;

public sealed partial class GovernanceDocumentLinter
{
    private static readonly string[] ExcludedDirectoryNames =
    [
        ".claude", ".git", ".artifacts", ".tooling", "bin", "obj", "node_modules", "test-results"
    ];

    private readonly string _repositoryRoot;
    private readonly GovernanceManifestService _manifestService;
    private readonly bool _checkGeneratedDocuments;

    public GovernanceDocumentLinter(string repositoryRoot, bool checkGeneratedDocuments = true)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _manifestService = new GovernanceManifestService(_repositoryRoot);
        _checkGeneratedDocuments = checkGeneratedDocuments;
    }

    public GovernanceLintReport Lint(DateTimeOffset now)
    {
        var findings = new List<GovernanceFinding>();
        GovernanceManifest manifest;
        try
        {
            manifest = _manifestService.LoadAndValidate();
        }
        catch (GovernanceManifestException exception)
        {
            findings.Add(Error("GOV001", exception.Message, "governance/manifest.yaml"));
            return new GovernanceLintReport(findings);
        }

        ValidateIdsAndPaths(manifest, findings);
        ValidateDocuments(manifest, findings, now);
        ValidateDependencies(manifest, findings);
        ValidateCanonicalConflicts(manifest, findings);
        ValidateMarkdownCoverage(manifest, findings);
        ValidateTokenBudget(manifest, findings);
        if (_checkGeneratedDocuments)
        {
            findings.AddRange(new GovernanceDocumentGenerator(_repositoryRoot).Check());
        }

        return new GovernanceLintReport(findings
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(finding => finding.Path, StringComparer.Ordinal)
            .ToArray());
    }

    private static void ValidateIdsAndPaths(GovernanceManifest manifest, List<GovernanceFinding> findings)
    {
        foreach (var duplicate in manifest.Documents.GroupBy(document => document.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            findings.Add(Error("GOV002", $"Duplicate document id '{duplicate.Key}'.", "governance/manifest.yaml"));
        }

        foreach (var duplicate in manifest.Documents.GroupBy(document => document.Path, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            findings.Add(Error("GOV003", $"Duplicate document path '{duplicate.Key}'.", "governance/manifest.yaml"));
        }
    }

    private void ValidateDocuments(
        GovernanceManifest manifest,
        List<GovernanceFinding> findings,
        DateTimeOffset now)
    {
        var knownOwners = manifest.KnownOwners.ToHashSet(StringComparer.Ordinal);
        foreach (var document in manifest.Documents)
        {
            var absolutePath = ResolveRepositoryPath(document.Path, findings, document.Path, "GOV004");
            if (absolutePath is null || !File.Exists(absolutePath))
            {
                findings.Add(Error("GOV005", "Manifest document does not exist.", document.Path));
                continue;
            }

            var content = File.ReadAllText(absolutePath);
            var checksum = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(absolutePath)))}";
            if (!string.Equals(checksum, document.Checksum, StringComparison.Ordinal))
            {
                findings.Add(Error("GOV006", "Document checksum diverges from the manifest; synchronize computed fields.", document.Path));
            }

            if (document.ContainsSecrets)
            {
                findings.Add(Error("GOV007", "containsSecrets must always be false; document only opaque secret references.", document.Path));
            }

            if (!knownOwners.Contains(document.Owner))
            {
                findings.Add(Warning("GOV101", $"Unknown owner '{document.Owner}'.", document.Path));
            }

            if (document.Category == "rule" && string.IsNullOrWhiteSpace(document.Owner))
            {
                findings.Add(Error("GOV008", "Canonical rule has no owner.", document.Path));
            }

            if (document.Category == "rule" && document.EnforcedBy.Count == 0)
            {
                findings.Add(Error("GOV009", "Canonical rule has no declared enforcement.", document.Path));
            }

            if (document.Status is DocumentStatus.Superseded or DocumentStatus.Historical &&
                document.LoadPolicy is DocumentLoadPolicy.Always or DocumentLoadPolicy.Entry or DocumentLoadPolicy.Bundle)
            {
                findings.Add(Error("GOV010", "Superseded or historical document is loadable.", document.Path));
            }

            if (document.Status == DocumentStatus.Active && document.Authority != DocumentAuthority.Historical)
            {
                if (ActiveAbsolutePathRegex().IsMatch(content))
                {
                    findings.Add(Error("GOV011", "Active document contains a user, machine or temporary absolute path.", document.Path));
                }

                ValidateInternalLinks(document, content, findings);
            }

            if (document.ReviewDueAt is not null &&
                DateTimeOffset.Parse(document.ReviewDueAt, System.Globalization.CultureInfo.InvariantCulture) < now)
            {
                findings.Add(Warning("GOV102", $"Review was due at {document.ReviewDueAt}.", document.Path));
            }

            if (document.Authority == DocumentAuthority.Evidence &&
                GovernanceManifestSynchronizer.EstimateTokens(content) < 30)
            {
                findings.Add(Warning("GOV103", "Evidence is below the minimum useful content threshold.", document.Path));
            }

            if (document.Status == DocumentStatus.Active &&
                document.Authority != DocumentAuthority.Historical &&
                document.LoadPolicy == DocumentLoadPolicy.Never)
            {
                findings.Add(Warning("GOV104", "Active document has no evidence of use because its load policy is never.", document.Path));
            }

            ValidateSourceOfTruth(document, findings);
            ValidatePromptMetadata(document, content, findings);
        }
    }

    private void ValidateSourceOfTruth(GovernanceDocument document, List<GovernanceFinding> findings)
    {
        var sourcePath = ResolveRepositoryPath(document.SourceOfTruth, findings, document.Path, "GOV012");
        if (sourcePath is null || !File.Exists(sourcePath))
        {
            findings.Add(Error("GOV013", "Required source of truth is missing or outside the repository.", document.Path));
        }
    }

    private static void ValidatePromptMetadata(
        GovernanceDocument document,
        string content,
        List<GovernanceFinding> findings)
    {
        if (document.Category != "prompt" || document.Status != DocumentStatus.Active)
        {
            return;
        }

        var required = new[] { "id:", "version:", "createdAt:", "source:", "checksum:" };
        if (!content.StartsWith("---\n", StringComparison.Ordinal) || required.Any(field => !content.Contains($"\n{field}", StringComparison.Ordinal)))
        {
            findings.Add(Error("GOV014", "Active prompt is missing versioned provenance frontmatter.", document.Path));
        }
    }

    private void ValidateInternalLinks(
        GovernanceDocument document,
        string content,
        List<GovernanceFinding> findings)
    {
        foreach (Match match in MarkdownLinkRegex().Matches(content))
        {
            var target = match.Groups["target"].Value.Trim().Trim('<', '>');
            if (target.Length == 0 || target.StartsWith('#') ||
                Uri.TryCreate(target, UriKind.Absolute, out _))
            {
                continue;
            }

            var pathPart = Uri.UnescapeDataString(target.Split('#', 2)[0]);
            if (pathPart.Length == 0)
            {
                continue;
            }

            var documentDirectory = Path.GetDirectoryName(Resolve(document.Path))!;
            var resolved = Path.GetFullPath(Path.Combine(documentDirectory, pathPart.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInsideRepository(resolved) || (!File.Exists(resolved) && !Directory.Exists(resolved)))
            {
                findings.Add(Error("GOV015", $"Broken internal link '{target}'.", document.Path));
            }
        }
    }

    private static void ValidateCanonicalConflicts(
        GovernanceManifest manifest,
        List<GovernanceFinding> findings)
    {
        var ids = manifest.Documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in manifest.Documents
                     .Where(document => document.Authority == DocumentAuthority.Canonical && document.Status == DocumentStatus.Active)
                     .GroupBy(document => (document.Topic, document.Scope)))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            var entries = group.ToArray();
            var hasSupersession = entries.Any(entry => entry.Supersedes.Any(ids.Contains));
            if (!hasSupersession)
            {
                findings.Add(Error(
                    "GOV016",
                    $"Multiple canonical documents cover topic '{group.Key.Topic}' and scope '{group.Key.Scope}' without supersession.",
                    "governance/manifest.yaml"));
            }
        }
    }

    private static void ValidateDependencies(
        GovernanceManifest manifest,
        List<GovernanceFinding> findings)
    {
        var byId = manifest.Documents
            .GroupBy(document => document.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var document in manifest.Documents)
        {
            foreach (var dependency in document.Dependencies.Concat(document.Supersedes).Concat(document.Related))
            {
                if (!byId.ContainsKey(dependency))
                {
                    findings.Add(Error("GOV017", $"Referenced document id '{dependency}' does not exist.", document.Path));
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in manifest.Documents)
        {
            Visit(document.Id, byId, visiting, visited, findings);
        }
    }

    private static void Visit(
        string id,
        IReadOnlyDictionary<string, GovernanceDocument> byId,
        ISet<string> visiting,
        ISet<string> visited,
        List<GovernanceFinding> findings)
    {
        if (visited.Contains(id) || !byId.TryGetValue(id, out var document))
        {
            return;
        }

        if (!visiting.Add(id))
        {
            findings.Add(Error("GOV018", $"Forbidden dependency cycle includes '{id}'.", document.Path));
            return;
        }

        foreach (var dependency in document.Dependencies)
        {
            Visit(dependency, byId, visiting, visited, findings);
        }

        visiting.Remove(id);
        visited.Add(id);
    }

    private void ValidateMarkdownCoverage(GovernanceManifest manifest, List<GovernanceFinding> findings)
    {
        var manifestPaths = manifest.Documents.Select(document => document.Path).ToHashSet(StringComparer.Ordinal);
        var allowlist = manifest.MarkdownAllowlist.Select(entry => entry.Path).ToArray();
        foreach (var markdown in Directory.EnumerateFiles(_repositoryRoot, "*.md", SearchOption.AllDirectories))
        {
            if (IsExcluded(markdown))
            {
                continue;
            }

            var relative = Path.GetRelativePath(_repositoryRoot, markdown).Replace(Path.DirectorySeparatorChar, '/');
            if (!manifestPaths.Contains(relative) && !allowlist.Any(pattern => GlobMatches(pattern, relative)))
            {
                findings.Add(Error("GOV019", "Markdown document is not represented in the manifest or explicit allowlist.", relative));
            }
        }
    }

    private static void ValidateTokenBudget(GovernanceManifest manifest, List<GovernanceFinding> findings)
    {
        var alwaysLoaded = manifest.Documents
            .Where(document => document.Status == DocumentStatus.Active &&
                document.LoadPolicy is DocumentLoadPolicy.Always or DocumentLoadPolicy.Entry)
            .Sum(document => document.TokenEstimate);
        if (alwaysLoaded > manifest.TokenBudget)
        {
            findings.Add(Warning(
                "GOV105",
                $"Always/entry context estimate {alwaysLoaded} exceeds budget {manifest.TokenBudget}.",
                "governance/manifest.yaml"));
        }
    }

    private string? ResolveRepositoryPath(
        string relativePath,
        List<GovernanceFinding> findings,
        string findingPath,
        string code)
    {
        if (Path.IsPathRooted(relativePath))
        {
            findings.Add(Error(code, "Path must be repository-relative.", findingPath));
            return null;
        }

        var resolved = Resolve(relativePath);
        if (!IsInsideRepository(resolved))
        {
            findings.Add(Error(code, "Path escapes the repository root.", findingPath));
            return null;
        }

        return resolved;
    }

    private bool IsInsideRepository(string path) =>
        path.StartsWith(_repositoryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        string.Equals(path, _repositoryRoot, StringComparison.Ordinal);

    private static bool GlobMatches(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant);
    }

    private static bool IsExcluded(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => ExcludedDirectoryNames.Contains(segment, StringComparer.Ordinal));
    }

    private string Resolve(string relativePath) => Path.GetFullPath(Path.Combine(
        _repositoryRoot,
        relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static GovernanceFinding Error(string code, string message, string? path = null) =>
        new(GovernanceFindingSeverity.Error, code, message, path);

    private static GovernanceFinding Warning(string code, string message, string? path = null) =>
        new(GovernanceFindingSeverity.Warning, code, message, path);

    [GeneratedRegex(@"\[[^\]]+\]\((?<target>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(?:/(?:Users|home|tmp|private/tmp)/|~/Downloads(?:/|\b)|[A-Za-z]:\\)", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveAbsolutePathRegex();
}
