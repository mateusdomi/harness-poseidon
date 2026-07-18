using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Projects.Domain;

public sealed record ProjectBrand(string? LogoUrl, string? PrimaryColor, string? SecondaryColor, string? Typography);

public sealed record Project(
    string Id,
    string OrganizationId,
    string Name,
    string Key,
    string Description,
    string State,
    string Criticality,
    string? RepositoryUrl,
    string RepositoryProvider,
    string DefaultBranch,
    IReadOnlyList<string> Technologies,
    ProjectBrand Brand,
    IReadOnlyList<string> MemberProfileIds,
    long ConfigVersion,
    string ChiefAgentId,
    string OperationMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    long Version)
{
    private static readonly IReadOnlySet<string> States =
        new HashSet<string>(["active", "paused", "archived"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Criticalities =
        new HashSet<string>(["low", "medium", "high", "critical"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> RepositoryProviders =
        new HashSet<string>(["github", "gitlab", "bitbucket", "local", "other"], StringComparer.Ordinal);

    public static Project Create(
        string id,
        string organizationId,
        string chiefAgentId,
        string ownerProfileId,
        string name,
        string key,
        string description,
        string? criticality,
        string? repositoryUrl,
        string? repositoryProvider,
        string? defaultBranch,
        IReadOnlyList<string>? technologies,
        ProjectBrand brand,
        IReadOnlyList<string>? memberProfileIds,
        DateTimeOffset occurredAt) =>
        new(
            RequiredId(id, nameof(id)),
            RequiredId(organizationId, nameof(organizationId)),
            Required(name, 200, nameof(name)),
            NormalizeKey(key),
            Required(description, 4_000, nameof(description)),
            "active",
            Choice(criticality ?? "medium", Criticalities, nameof(criticality)),
            Optional(repositoryUrl, 2_048, nameof(repositoryUrl)),
            Choice(repositoryProvider ?? "local", RepositoryProviders, nameof(repositoryProvider)),
            Required(defaultBranch ?? "main", 200, nameof(defaultBranch)),
            NormalizeList(technologies ?? [], 50, 100, nameof(technologies)),
            NormalizeBrand(brand),
            NormalizeIds(memberProfileIds is { Count: > 0 } ? memberProfileIds : [ownerProfileId]),
            1,
            RequiredId(chiefAgentId, nameof(chiefAgentId)),
            "manual",
            RequireUtc(occurredAt),
            occurredAt,
            1);

    public Project Update(
        string name,
        string description,
        string state,
        string criticality,
        string? repositoryUrl,
        string repositoryProvider,
        string defaultBranch,
        IReadOnlyList<string> technologies,
        ProjectBrand brand,
        IReadOnlyList<string> memberProfileIds,
        bool configurationChanged,
        DateTimeOffset occurredAt) =>
        this with
        {
            Name = Required(name, 200, nameof(name)),
            Description = Required(description, 4_000, nameof(description)),
            State = Choice(state, States, nameof(state)),
            Criticality = Choice(criticality, Criticalities, nameof(criticality)),
            RepositoryUrl = Optional(repositoryUrl, 2_048, nameof(repositoryUrl)),
            RepositoryProvider = Choice(repositoryProvider, RepositoryProviders, nameof(repositoryProvider)),
            DefaultBranch = Required(defaultBranch, 200, nameof(defaultBranch)),
            Technologies = NormalizeList(technologies, 50, 100, nameof(technologies)),
            Brand = NormalizeBrand(brand),
            MemberProfileIds = NormalizeIds(memberProfileIds),
            ConfigVersion = configurationChanged ? checked(ConfigVersion + 1) : ConfigVersion,
            LastActivityAt = RequireUtc(occurredAt),
            Version = checked(Version + 1),
        };

    private static ProjectBrand NormalizeBrand(ProjectBrand brand)
    {
        ArgumentNullException.ThrowIfNull(brand);
        return new ProjectBrand(
            Optional(brand.LogoUrl, 2_048, nameof(brand)),
            Optional(brand.PrimaryColor, 32, nameof(brand)),
            Optional(brand.SecondaryColor, 32, nameof(brand)),
            Optional(brand.Typography, 200, nameof(brand)));
    }

    private static string NormalizeKey(string value)
    {
        var key = Required(value, 30, nameof(value)).ToUpperInvariant();
        if (key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Project key must use ASCII letters, numbers, or hyphens.", nameof(value));
        }

        return key;
    }

    private static string[] NormalizeIds(IReadOnlyList<string> values)
    {
        var ids = NormalizeList(values, 200, 26, nameof(values));
        if (ids.Any(value => !UlidValue.TryParse(value, out _)))
        {
            throw new ArgumentException("Member IDs must be canonical ULIDs.", nameof(values));
        }

        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string RequiredId(string value, string name) =>
        UlidValue.TryParse(value, out var id)
            ? id.ToString()
            : throw new ArgumentException("Value must be a canonical ULID.", name);

    private static string[] NormalizeList(IReadOnlyList<string> values, int maximumItems, int maximumLength, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximumItems)
        {
            throw new ArgumentException($"Collection exceeds {maximumItems} items.", name);
        }

        return values.Select(item => Required(item, maximumLength, name)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Choice(string value, IReadOnlySet<string> choices, string name)
    {
        var normalized = Required(value, 100, name);
        return choices.Contains(normalized) ? normalized : throw new ArgumentException("Value is not supported.", name);
    }

    private static string Required(string value, int maximumLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : throw new ArgumentException($"Value exceeds {maximumLength} characters.", name);
    }

    private static string? Optional(string? value, int maximumLength, string name)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        if (normalized.Length == 0) return null;
        return normalized.Length <= maximumLength ? normalized : throw new ArgumentException($"Value exceeds {maximumLength} characters.", name);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
