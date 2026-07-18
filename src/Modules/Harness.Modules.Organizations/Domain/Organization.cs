namespace Harness.Modules.Organizations.Domain;

public sealed record OrganizationBrand(
    string? LogoUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? Typography)
{
    public static OrganizationBrand Create(
        string? logoUrl,
        string? primaryColor,
        string? secondaryColor,
        string? typography) =>
        new(
            Optional(logoUrl, 2_048, nameof(logoUrl)),
            Optional(primaryColor, 32, nameof(primaryColor)),
            Optional(secondaryColor, 32, nameof(secondaryColor)),
            Optional(typography, 200, nameof(typography)));

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value exceeds {maximumLength} characters.", parameterName);
        }

        return normalized;
    }
}

public sealed record OrganizationPolicy(string Key, string Description, bool Enabled);

public sealed record Organization(
    string Id,
    string Name,
    string Slug,
    string Plan,
    OrganizationBrand Brand,
    IReadOnlyList<string> DefaultWorkflowTemplateIds,
    IReadOnlyList<string> TemplateKeys,
    IReadOnlyList<OrganizationPolicy> Policies,
    DateTimeOffset CreatedAt,
    long Version)
{
    public static Organization Create(
        string id,
        string name,
        string slug,
        string? plan,
        OrganizationBrand brand,
        DateTimeOffset occurredAt) =>
        new(
            Required(id, 26, nameof(id)),
            Required(name, 200, nameof(name)),
            NormalizeSlug(slug),
            Required(plan ?? "personal", 100, nameof(plan)),
            brand ?? throw new ArgumentNullException(nameof(brand)),
            [],
            [],
            [],
            RequireUtc(occurredAt),
            1);

    public Organization Update(
        string name,
        string slug,
        string plan,
        OrganizationBrand brand) =>
        this with
        {
            Name = Required(name, 200, nameof(name)),
            Slug = NormalizeSlug(slug),
            Plan = Required(plan, 100, nameof(plan)),
            Brand = brand ?? throw new ArgumentNullException(nameof(brand)),
            Version = checked(Version + 1),
        };

    private static string NormalizeSlug(string value)
    {
        var normalized = Required(value, 100, nameof(value)).ToLowerInvariant();
        if (normalized[0] == '-' || normalized[^1] == '-' ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "Slug must contain only lowercase ASCII letters, numbers, and internal hyphens.",
                nameof(value));
        }

        return normalized;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value exceeds {maximumLength} characters.", parameterName);
        }

        return normalized;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Timestamp must be UTC.");
        }

        return value;
    }
}
