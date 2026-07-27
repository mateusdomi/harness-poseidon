namespace Harness.SharedKernel.Memory;

/// <summary>
/// Documento vetorial indexado na memória semântica (Fase 6 / N6).
/// </summary>
public sealed record VectorDocumentRecord(
    string Id,
    string TenantId,
    string ProjectId,
    string DocumentType,
    string Content,
    IReadOnlyList<float> Embedding,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);

/// <summary>
/// Resultado de busca por similaridade vetorial com pontuação coseno.
/// </summary>
public sealed record VectorSearchResult(
    VectorDocumentRecord Document,
    double Score);

/// <summary>
/// Interface para índice de vetores semânticos com suporte a SQLite FTS5 / in-memory e pgvector Postgres (Fase 6 / N6).
/// </summary>
public interface IVectorIndex
{
    Task IndexAsync(VectorDocumentRecord document, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorDocumentRecord>> ListAsync(
        string tenantId,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string tenantId,
        IReadOnlyList<float> queryEmbedding,
        int topK = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string tenantId, string documentId, CancellationToken cancellationToken = default);
}
