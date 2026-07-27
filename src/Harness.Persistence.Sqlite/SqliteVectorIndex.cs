using System.Globalization;
using System.Text.Json;
using Harness.SharedKernel.Memory;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteVectorIndex(SqliteWriteDispatcher dispatcher) : IVectorIndex
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task IndexAsync(VectorDocumentRecord document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO vector_embeddings (
                    id, tenant_id, project_id, document_type, content,
                    embedding_json, metadata_json, created_at
                ) VALUES (
                    $id, $tenantId, $projectId, $documentType, $content,
                    $embeddingJson, $metadataJson, $createdAt
                ) ON CONFLICT(id) DO UPDATE SET
                    content = EXCLUDED.content,
                    embedding_json = EXCLUDED.embedding_json,
                    metadata_json = EXCLUDED.metadata_json,
                    created_at = EXCLUDED.created_at;
                """;

            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$tenantId", document.TenantId);
            command.Parameters.AddWithValue("$projectId", document.ProjectId);
            command.Parameters.AddWithValue("$documentType", document.DocumentType);
            command.Parameters.AddWithValue("$content", document.Content);
            command.Parameters.AddWithValue("$embeddingJson", JsonSerializer.Serialize(document.Embedding));
            command.Parameters.AddWithValue("$metadataJson", JsonSerializer.Serialize(document.Metadata));
            command.Parameters.AddWithValue("$createdAt", document.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string tenantId,
        IReadOnlyList<float> queryEmbedding,
        int topK = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            var documents = await ListCoreAsync(connection, tenantId, projectId: null, token);

            var results = new List<VectorSearchResult>();
            foreach (var document in documents)
            {
                var score = ComputeCosineSimilarity(queryEmbedding, document.Embedding);
                if (score >= minScore)
                {
                    results.Add(new VectorSearchResult(document, score));
                }
            }

            return (IReadOnlyList<VectorSearchResult>)results
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .ToList();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<VectorDocumentRecord>> ListAsync(
        string tenantId,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return _dispatcher.ExecuteAsync<IReadOnlyList<VectorDocumentRecord>>(
            (connection, token) => ListCoreAsync(connection, tenantId, projectId, token),
            cancellationToken);
    }

    public Task DeleteAsync(string tenantId, string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(documentId);

        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM vector_embeddings WHERE tenant_id = $tenantId AND id = $id;";
            command.Parameters.AddWithValue("$tenantId", tenantId);
            command.Parameters.AddWithValue("$id", documentId);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    private static double ComputeCosineSimilarity(IReadOnlyList<float> v1, IReadOnlyList<float> v2)
    {
        if (v1.Count == 0 || v2.Count == 0 || v1.Count != v2.Count)
        {
            return 0.0;
        }

        double dot = 0.0;
        double norm1 = 0.0;
        double norm2 = 0.0;

        for (int i = 0; i < v1.Count; i++)
        {
            dot += v1[i] * v2[i];
            norm1 += v1[i] * v1[i];
            norm2 += v2[i] * v2[i];
        }

        if (norm1 <= 0.0 || norm2 <= 0.0)
        {
            return 0.0;
        }

        return Math.Clamp(dot / (Math.Sqrt(norm1) * Math.Sqrt(norm2)), 0.0, 1.0);
    }

    private static async Task<IReadOnlyList<VectorDocumentRecord>> ListCoreAsync(
        SqliteConnection connection,
        string tenantId,
        string? projectId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, project_id, document_type, content,
                   embedding_json, metadata_json, created_at
            FROM vector_embeddings
            WHERE tenant_id = $tenantId
              AND ($projectId IS NULL OR project_id = $projectId)
            ORDER BY created_at, id;
            """;
        command.Parameters.AddWithValue("$tenantId", tenantId);
        command.Parameters.AddWithValue("$projectId", projectId is null ? DBNull.Value : projectId);

        var documents = new List<VectorDocumentRecord>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var embedding = JsonSerializer.Deserialize<float[]>(reader.GetString(5)) ?? [];
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? new();
            documents.Add(new VectorDocumentRecord(
                Id: reader.GetString(0),
                TenantId: reader.GetString(1),
                ProjectId: reader.GetString(2),
                DocumentType: reader.GetString(3),
                Content: reader.GetString(4),
                Embedding: embedding,
                Metadata: metadata,
                CreatedAt: DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
        }

        return documents;
    }
}
