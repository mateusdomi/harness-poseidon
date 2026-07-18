namespace Harness.Persistence.Abstractions.Documents;

public interface IDocumentCatalogStore
{
    Task<IReadOnlyList<DocumentCatalogRecord>> ListDocumentsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default);

    Task<DocumentCatalogRecord?> GetDocumentAsync(
        string tenantId, string documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentVersionCatalogRecord>> ListVersionsAsync(
        string tenantId, string? documentId, string? afterId, int limit,
        CancellationToken cancellationToken = default);

    Task<DocumentVersionCatalogRecord?> GetVersionAsync(
        string tenantId, string versionId, CancellationToken cancellationToken = default);
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

public sealed record DocumentVersionCatalogRecord(
    string Id, string DocumentId, int Version, string CatalogPath, string ContentHash,
    string AuthorKind, string? AuthorId, DateTimeOffset CreatedAt);
