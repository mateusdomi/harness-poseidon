using System.Text.Json.Serialization;

namespace Harness.Modules.Projects.Contracts;

public sealed record ProjectBrandContract(string? LogoUrl, string? PrimaryColor, string? SecondaryColor, string? Typography)
{
    public static ProjectBrandContract Empty { get; } = new(null, null, null, null);
}
public sealed record PrototypingWaiverContract(string Reason, DateTimeOffset GrantedAt);
public sealed record PrototypingConfigContract(string Mode, PrototypingWaiverContract? Waiver)
{
    public static PrototypingConfigContract Default { get; } = new("autonomousGeneration", null);
}

public sealed record ProjectContract(
    string Id, string OrganizationId, string Name, string Key, string Description,
    string State, string Criticality, string? RepositoryUrl, string RepositoryProvider,
    string DefaultBranch, IReadOnlyList<string> Technologies, ProjectBrandContract Brand,
    IReadOnlyList<string> MemberProfileIds, long ConfigVersion, string ChiefAgentId,
    string OperationMode, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, long Version)
{
    public PrototypingConfigContract Prototyping { get; init; } = PrototypingConfigContract.Default;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateProjectRequest
{
    public required string OrganizationId { get; init; }
    public required string Name { get; init; }
    public required string Key { get; init; }
    public required string Description { get; init; }
    public string? Criticality { get; init; }
    public string? RepositoryUrl { get; init; }
    public string? RepositoryProvider { get; init; }
    public string? DefaultBranch { get; init; }
    public IReadOnlyList<string>? Technologies { get; init; }
    public ProjectBrandContract? Brand { get; init; }
    public IReadOnlyList<string>? MemberProfileIds { get; init; }

    /// <summary>
    /// Pré-seleção do workflow na criação (GP-09). Ausente, nulo ou vazio vincula o template
    /// recomendado publicado (o "Software Delivery Standard"); um ULID vincula o template
    /// informado. Projetos operacionais não nascem sem workflow.
    /// </summary>
    public string? WorkflowTemplateId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateProjectRequest
{
    private string? _name; private string? _description; private string? _state; private string? _criticality;
    private string? _repositoryUrl; private string? _repositoryProvider; private string? _defaultBranch;
    private IReadOnlyList<string>? _technologies; private ProjectBrandContract? _brand;
    private IReadOnlyList<string>? _memberProfileIds;
    private PrototypingConfigContract? _prototyping;

    public string? Name { get => _name; init { _name = value; NameSpecified = true; } }
    public string? Description { get => _description; init { _description = value; DescriptionSpecified = true; } }
    public string? State { get => _state; init { _state = value; StateSpecified = true; } }
    public string? Criticality { get => _criticality; init { _criticality = value; CriticalitySpecified = true; } }
    public string? RepositoryUrl { get => _repositoryUrl; init { _repositoryUrl = value; RepositoryUrlSpecified = true; } }
    public string? RepositoryProvider { get => _repositoryProvider; init { _repositoryProvider = value; RepositoryProviderSpecified = true; } }
    public string? DefaultBranch { get => _defaultBranch; init { _defaultBranch = value; DefaultBranchSpecified = true; } }
    public IReadOnlyList<string>? Technologies { get => _technologies; init { _technologies = value; TechnologiesSpecified = true; } }
    public ProjectBrandContract? Brand { get => _brand; init { _brand = value; BrandSpecified = true; } }
    public IReadOnlyList<string>? MemberProfileIds { get => _memberProfileIds; init { _memberProfileIds = value; MemberProfileIdsSpecified = true; } }
    public PrototypingConfigContract? Prototyping { get => _prototyping; init { _prototyping = value; PrototypingSpecified = true; } }

    [JsonIgnore] public bool NameSpecified { get; private set; }
    [JsonIgnore] public bool DescriptionSpecified { get; private set; }
    [JsonIgnore] public bool StateSpecified { get; private set; }
    [JsonIgnore] public bool CriticalitySpecified { get; private set; }
    [JsonIgnore] public bool RepositoryUrlSpecified { get; private set; }
    [JsonIgnore] public bool RepositoryProviderSpecified { get; private set; }
    [JsonIgnore] public bool DefaultBranchSpecified { get; private set; }
    [JsonIgnore] public bool TechnologiesSpecified { get; private set; }
    [JsonIgnore] public bool BrandSpecified { get; private set; }
    [JsonIgnore] public bool MemberProfileIdsSpecified { get; private set; }
    [JsonIgnore] public bool PrototypingSpecified { get; private set; }

    [JsonIgnore]
    public bool AnySpecified => NameSpecified || DescriptionSpecified || StateSpecified || CriticalitySpecified ||
        RepositoryUrlSpecified || RepositoryProviderSpecified || DefaultBranchSpecified || TechnologiesSpecified ||
        BrandSpecified || MemberProfileIdsSpecified || PrototypingSpecified;

    [JsonIgnore]
    public bool ConfigurationSpecified => RepositoryUrlSpecified || RepositoryProviderSpecified ||
        DefaultBranchSpecified || TechnologiesSpecified || BrandSpecified || PrototypingSpecified;
}
