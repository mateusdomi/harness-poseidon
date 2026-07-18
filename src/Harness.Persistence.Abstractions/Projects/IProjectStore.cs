namespace Harness.Persistence.Abstractions.Projects;

public interface IProjectStore
{
    Task<ProjectRecord?> GetAsync(string tenantId, string projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectRecord>> ListAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<ProjectMutationResult> CreateAsync(ProjectCreateCommand command, CancellationToken cancellationToken = default);
    Task<ProjectMutationResult> UpdateAsync(ProjectUpdateCommand command, CancellationToken cancellationToken = default);
    Task<ProjectMutationResult> DeleteAsync(string tenantId, string projectId, long expectedVersion, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}

public sealed record ProjectBrandRecord(string? LogoUrl, string? PrimaryColor, string? SecondaryColor, string? Typography);
public sealed record ProjectPrototypingWaiverRecord(string Reason, DateTimeOffset GrantedAt);
public sealed record ProjectPrototypingRecord(string Mode, ProjectPrototypingWaiverRecord? Waiver)
{
    public static ProjectPrototypingRecord Default { get; } = new("autonomousGeneration", null);
}
public sealed record ProjectRecord(
    string TenantId, string Id, string OrganizationId, string Name, string Key, string Description,
    string State, string Criticality, string? RepositoryUrl, string RepositoryProvider, string DefaultBranch,
    IReadOnlyList<string> Technologies, ProjectBrandRecord Brand, IReadOnlyList<string> MemberProfileIds,
    long ConfigVersion, string ChiefAgentId, string OperationMode, DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt, long Version)
{
    public ProjectPrototypingRecord Prototyping { get; init; } = ProjectPrototypingRecord.Default;
}
public sealed record ProjectCreateCommand(string TenantId, ProjectRecord Project, DateTimeOffset OccurredAt);
public sealed record ProjectUpdateCommand(ProjectRecord Project, long ExpectedVersion);
public enum ProjectMutationStatus { Applied, AlreadyExists, OrganizationNotFound, NotFound, VersionConflict }
public sealed record ProjectMutationResult(ProjectMutationStatus Status, ProjectRecord? Project = null);
