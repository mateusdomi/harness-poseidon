using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Harness.Modules.Governance.Documentation;

public enum DocumentAuthority
{
    Canonical,
    Adapter,
    Generated,
    Operational,
    Reference,
    Evidence,
    Historical
}

public enum DocumentAudience
{
    Human,
    Agent,
    Runtime
}

public enum DocumentLoadPolicy
{
    Always,
    Entry,
    Bundle,
    OnDemand,
    Never
}

public enum DocumentStatus
{
    Active,
    Draft,
    Deprecated,
    Superseded,
    Historical
}

public sealed class GovernanceManifest
{
    public required string ManifestVersion { get; init; }

    public required string LastGeneratedAt { get; set; }

    public required int TokenBudget { get; init; }

    public required List<string> KnownOwners { get; init; }

    public required List<MarkdownAllowlistEntry> MarkdownAllowlist { get; init; }

    public required List<GovernanceDocument> Documents { get; init; }
}

public sealed class MarkdownAllowlistEntry
{
    public required string Path { get; init; }

    public required string Reason { get; init; }
}

public sealed class GovernanceDocument
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string Topic { get; init; }

    public required DocumentAuthority Authority { get; init; }

    public required string Scope { get; init; }

    public required List<DocumentAudience> Audience { get; init; }

    public required List<string> Providers { get; init; }

    public required List<string> Agents { get; init; }

    public required List<string> Workflows { get; init; }

    public required List<string> Phases { get; init; }

    public required List<string> TaskTypes { get; init; }

    public required List<string> RiskTiers { get; init; }

    public required List<string> PathGlobs { get; init; }

    [JsonPropertyName("load")]
    [YamlMember(Alias = "load", ApplyNamingConventions = false)]
    public required DocumentLoadPolicy LoadPolicy { get; init; }

    public required int Priority { get; init; }

    [JsonPropertyName("tokenCost")]
    [YamlMember(Alias = "tokenCost", ApplyNamingConventions = false)]
    public required int TokenEstimate { get; set; }

    public required string Owner { get; init; }

    public required DocumentStatus Status { get; init; }

    public required string Version { get; init; }

    public required string LastVerifiedAt { get; init; }

    public required string? ReviewDueAt { get; init; }

    public required List<string> Supersedes { get; init; }

    public required List<string> Dependencies { get; init; }

    public required List<string> Related { get; init; }

    public required List<string> EnforcedBy { get; init; }

    public required string Checksum { get; set; }

    public required bool ContainsSecrets { get; init; }

    public required bool Generated { get; init; }

    public required string SourceOfTruth { get; init; }
}
