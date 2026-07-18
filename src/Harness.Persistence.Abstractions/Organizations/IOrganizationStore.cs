namespace Harness.Persistence.Abstractions.Organizations;

public interface IOrganizationStore
{
    Task<OrganizationRecord?> GetAsync(
        string tenantId,
        string organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganizationRecord>> ListAsync(
        string tenantId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<OrganizationMutationResult> CreateAsync(
        OrganizationCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<OrganizationMutationResult> UpdateAsync(
        OrganizationUpdateCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationBrandRecord(
    string? LogoUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    string? Typography);

public sealed record OrganizationPolicyRecord(string Key, string Description, bool Enabled);

public sealed record OrganizationRecord(
    string TenantId,
    string Id,
    string Name,
    string Slug,
    string Plan,
    OrganizationBrandRecord Brand,
    IReadOnlyList<string> DefaultWorkflowTemplateIds,
    IReadOnlyList<string> TemplateKeys,
    IReadOnlyList<OrganizationPolicyRecord> Policies,
    DateTimeOffset CreatedAt,
    long Version);

public sealed record OrganizationCreateCommand(
    string TenantId,
    string OrganizationId,
    string Name,
    string Slug,
    string Plan,
    OrganizationBrandRecord Brand,
    DateTimeOffset OccurredAt);

public sealed record OrganizationUpdateCommand(
    string TenantId,
    string OrganizationId,
    string Name,
    string Slug,
    string Plan,
    OrganizationBrandRecord Brand,
    long ExpectedVersion);

public enum OrganizationMutationStatus
{
    Applied,
    AlreadyExists,
    NotFound,
    VersionConflict,
}

public sealed record OrganizationMutationResult(
    OrganizationMutationStatus Status,
    OrganizationRecord? Organization = null);
