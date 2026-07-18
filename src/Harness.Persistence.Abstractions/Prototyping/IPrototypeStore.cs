namespace Harness.Persistence.Abstractions.Prototyping;

public interface IPrototypeStore
{
    Task<IReadOnlyList<PrototypeRecord>> ListPrototypesAsync(string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<PrototypeRecord?> GetPrototypeAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<PrototypeRecord> CreatePrototypeAsync(PrototypeCreateCommand command, CancellationToken cancellationToken = default);
    Task<PrototypeRecord> TransitionPrototypeAsync(PrototypeTransitionCommand command, CancellationToken cancellationToken = default);
    Task DeletePrototypeAsync(PrototypeDeleteCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VisualReferenceRecord>> ListReferencesAsync(string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<VisualReferenceRecord?> GetReferenceAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<VisualReferenceRecord> CreateReferenceAsync(VisualReferenceCreateCommand command, CancellationToken cancellationToken = default);
    Task DeleteReferenceAsync(PrototypeDeleteCommand command, CancellationToken cancellationToken = default);
}

public sealed record PrototypeRecord(string Id, string ProjectId, string Name, string Description, string State, string? Url, string? ThumbnailUrl, string? SourceDocumentId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record VisualReferenceRecord(string Id, string ProjectId, string? PrototypeId, string Title, string ImageUrl, string Source, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt);
public sealed record PrototypeCreateCommand(string TenantId, string ActorProfileId, string Id, string ProjectId, string Name, string? Description, string? SourceDocumentId, DateTimeOffset OccurredAt);
public sealed record VisualReferenceCreateCommand(string TenantId, string ActorProfileId, string Id, string ProjectId, string? PrototypeId, string Title, string ImageUrl, string Source, IReadOnlyList<string>? Tags, DateTimeOffset OccurredAt);
public sealed record PrototypeTransitionCommand(string TenantId, string ActorProfileId, string Id, string State, string? Url, string? ThumbnailUrl, DateTimeOffset OccurredAt);
public sealed record PrototypeDeleteCommand(string TenantId, string ActorProfileId, string Id, DateTimeOffset OccurredAt);
public sealed class PrototypeValidationException(string detail) : Exception(detail);
public sealed class PrototypeNotFoundException(string resource) : Exception(resource) { public string Resource { get; } = resource; }
