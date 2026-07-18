using System.Text.Json.Serialization;

namespace Harness.Modules.Organizations.Contracts;

public sealed record OrganizationBrandContract(
    string? LogoUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? Typography)
{
    public static OrganizationBrandContract Empty { get; } = new(null, null, null, null);
}

public sealed record OrganizationPolicyContract(string Key, string Description, bool Enabled);

public sealed record OrganizationContract(
    string Id,
    string Name,
    string Slug,
    string Plan,
    OrganizationBrandContract Brand,
    IReadOnlyList<string> DefaultWorkflowTemplateIds,
    IReadOnlyList<string> TemplateKeys,
    IReadOnlyList<OrganizationPolicyContract> Policies,
    DateTimeOffset CreatedAt,
    long Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateOrganizationRequest
{
    public required string Name { get; init; }

    public required string Slug { get; init; }

    public string? Plan { get; init; }

    public OrganizationBrandContract? Brand { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateOrganizationRequest
{
    private string? _name;
    private string? _slug;
    private string? _plan;
    private OrganizationBrandContract? _brand;

    public string? Name
    {
        get => _name;
        init
        {
            _name = value;
            NameSpecified = true;
        }
    }

    public string? Slug
    {
        get => _slug;
        init
        {
            _slug = value;
            SlugSpecified = true;
        }
    }

    public string? Plan
    {
        get => _plan;
        init
        {
            _plan = value;
            PlanSpecified = true;
        }
    }

    public OrganizationBrandContract? Brand
    {
        get => _brand;
        init
        {
            _brand = value;
            BrandSpecified = true;
        }
    }

    [JsonIgnore]
    public bool NameSpecified { get; private set; }

    [JsonIgnore]
    public bool SlugSpecified { get; private set; }

    [JsonIgnore]
    public bool PlanSpecified { get; private set; }

    [JsonIgnore]
    public bool BrandSpecified { get; private set; }
}
