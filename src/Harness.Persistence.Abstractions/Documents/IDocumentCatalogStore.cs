namespace Harness.Persistence.Abstractions.Documents;

public interface IDocumentCatalogStore
{
    Task<IReadOnlyList<DocumentCatalogRecord>> ListDocumentsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default);

    Task<DocumentCatalogPageRecord> PageDocumentsAsync(
        string tenantId, DocumentCatalogPageQuery query,
        CancellationToken cancellationToken = default);

    Task<DocumentCatalogRecord?> GetDocumentAsync(
        string tenantId, string documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentVersionCatalogRecord>> ListVersionsAsync(
        string tenantId, string? documentId, string? afterId, int limit,
        CancellationToken cancellationToken = default);

    Task<DocumentVersionCatalogRecord?> GetVersionAsync(
        string tenantId, string versionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApprovalCatalogRecord>> ListApprovalsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default);

    Task<ApprovalCatalogRecord?> GetApprovalAsync(
        string tenantId, string approvalId, CancellationToken cancellationToken = default);

    Task<ApprovalCatalogRecord> CreateGeneralApprovalAsync(
        GeneralApprovalCreateCommand command, CancellationToken cancellationToken = default);

    Task<ApprovalCatalogRecord?> ResolveGeneralApprovalAsync(
        GeneralApprovalResolveCommand command, CancellationToken cancellationToken = default);
}

public interface IDocumentContentCatalog
{
    Task WriteAsync(string catalogPath, string body, string expectedHash,
        CancellationToken cancellationToken = default);
    Task<string> ReadAsync(string catalogPath, string expectedHash,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(string catalogPath, CancellationToken cancellationToken = default);
}

public sealed record DocumentCatalogRecord(
    string Id, string ProjectId, string Title, string Kind, string State, int CurrentVersion,
    IReadOnlyList<string> Classifications, string? PhaseName, bool Inconsistent,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long AggregateVersion);

public sealed record DocumentCatalogPageQuery(
    string? ProjectId, string? SearchPattern, string? Kind, string? State, string? PhaseName,
    string? Classification, bool OrphanOnly, bool? Inconsistent, int Offset, int Limit);

public sealed record DocumentCatalogPageRecord(
    IReadOnlyList<DocumentCatalogRecord> Items, int Total);

public sealed record DocumentVersionCatalogRecord(
    string Id, string DocumentId, int Version, string CatalogPath, string ContentHash,
    string AuthorKind, string? AuthorId, DateTimeOffset CreatedAt);

public sealed record ApprovalCatalogRecord(
    string Id, string ProjectId, string? GateId, string? TaskId, string? DocumentId,
    string Title, string Description, string Priority, DateTimeOffset? DueAt, string State,
    string RequestedByAgentId, DateTimeOffset RequestedAt, string? ResolvedByProfileId,
    DateTimeOffset? ResolvedAt, string? ResolutionNote, long AggregateVersion);

public sealed record GeneralApprovalCreateCommand(
    string TenantId, string Id, string ProjectId, string? GateId, string? TaskId,
    string Title, string Description, string Priority, DateTimeOffset? DueAt,
    string RequestedByAgentId, DateTimeOffset OccurredAt);

public sealed record GeneralApprovalResolveCommand(
    string TenantId, string Id, string Decision, string ResolvedByProfileId,
    string? Note, DateTimeOffset OccurredAt);

public sealed class ApprovalReferenceNotFoundException(string reference)
    : Exception($"Approval reference '{reference}' does not exist.")
{
    public string Reference { get; } = reference;
}
