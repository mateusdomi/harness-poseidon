using System.Security.Cryptography;

namespace Harness.Modules.Governance.Documentation;

public enum StaleDocumentFindingKind
{
    ReviewOverdue,
    SourceChanged,
    ChecksumDrift,
    DependencyDrift,
    EnforcementInactive,
    AdapterOutdated,
    NeverSelected,
    FrequentlyTruncated,
    CompetingSources,
}

public sealed record DocumentUsageSnapshot(
    string DocumentId,
    int SelectedCount,
    int TruncatedCount,
    DateTimeOffset? LastSelectedAt);

public sealed record StaleDocumentFinding(
    string FindingId,
    string DocumentId,
    StaleDocumentFindingKind Kind,
    string Detail,
    string RecommendedTask,
    DateTimeOffset DetectedAt);

public sealed record StaleDocumentDetectorOptions
{
    public bool Enabled { get; init; } = true;
}

public sealed class StaleDocumentDetector
{
    private readonly string _repositoryRoot;
    private readonly GovernanceManifestService _manifestService;
    private readonly StaleDocumentDetectorOptions _options;

    public StaleDocumentDetector(string repositoryRoot, StaleDocumentDetectorOptions? options = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _manifestService = new GovernanceManifestService(_repositoryRoot);
        _options = options ?? new StaleDocumentDetectorOptions();
    }

    public IReadOnlyList<StaleDocumentFinding> Detect(
        DateTimeOffset now,
        IReadOnlyList<DocumentUsageSnapshot> usage,
        IReadOnlySet<string> activeEnforcements)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(activeEnforcements);
        if (!_options.Enabled) return [];
        var manifest = _manifestService.LoadAndValidate();
        var byUsage = usage.ToDictionary(item => item.DocumentId, StringComparer.Ordinal);
        var findings = new List<StaleDocumentFinding>();
        foreach (var document in manifest.Documents.Where(document => document.Status == DocumentStatus.Active))
        {
            var path = Path.Combine(_repositoryRoot, document.Path.Replace('/', Path.DirectorySeparatorChar));
            if (document.ReviewDueAt is not null &&
                DateTimeOffset.TryParse(document.ReviewDueAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var due) && due < now)
            {
                Add(findings, document, StaleDocumentFindingKind.ReviewOverdue, "reviewDueAt is overdue.", now);
            }

            if (DateTimeOffset.TryParse(document.LastVerifiedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var verified) &&
                File.GetLastWriteTimeUtc(path) > verified.UtcDateTime)
            {
                Add(findings, document, StaleDocumentFindingKind.SourceChanged, "Source changed after last verification.", now);
            }

            var checksum = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";
            if (!string.Equals(checksum, document.Checksum, StringComparison.Ordinal))
            {
                Add(findings, document, StaleDocumentFindingKind.ChecksumDrift, "Manifest checksum differs from source.", now);
            }

            if (document.Dependencies.Any(dependency => manifest.Documents.All(candidate => candidate.Id != dependency)))
            {
                Add(findings, document, StaleDocumentFindingKind.DependencyDrift, "A declared dependency is missing.", now);
            }

            if (document.Category == "rule" &&
                !document.EnforcedBy.Any(activeEnforcements.Contains))
            {
                Add(findings, document, StaleDocumentFindingKind.EnforcementInactive, "Rule has no active enforcement.", now);
            }

            if (document.Authority == DocumentAuthority.Adapter && document.Generated &&
                !string.Equals(checksum, document.Checksum, StringComparison.Ordinal))
            {
                Add(findings, document, StaleDocumentFindingKind.AdapterOutdated, "Generated adapter is stale.", now);
            }

            if (!byUsage.TryGetValue(document.Id, out var value) || value.SelectedCount == 0)
            {
                Add(findings, document, StaleDocumentFindingKind.NeverSelected, "No receipt selected this active document.", now);
            }
            else if (value.SelectedCount >= 3 && value.TruncatedCount * 2 >= value.SelectedCount)
            {
                Add(findings, document, StaleDocumentFindingKind.FrequentlyTruncated, "Document is truncated in at least half of selections.", now);
            }
        }

        foreach (var group in manifest.Documents
                     .Where(document => document.Status == DocumentStatus.Active && document.Authority == DocumentAuthority.Canonical)
                     .GroupBy(document => $"{document.Topic}:{document.Scope}", StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (var document in group)
            {
                Add(findings, document, StaleDocumentFindingKind.CompetingSources, $"Competing canonical sources: {group.Key}.", now);
            }
        }

        return findings
            .OrderBy(finding => finding.DocumentId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Kind)
            .ToArray();
    }

    private static void Add(
        List<StaleDocumentFinding> findings,
        GovernanceDocument document,
        StaleDocumentFindingKind kind,
        string detail,
        DateTimeOffset now) => findings.Add(new StaleDocumentFinding(
        $"{document.Id}:{kind.ToString().ToLowerInvariant()}",
        document.Id,
        kind,
        detail,
        $"Review {document.Path}; update verification metadata or enforcement evidence without deleting automatically.",
        now));
}
