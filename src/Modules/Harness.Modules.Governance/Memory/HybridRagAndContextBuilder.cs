using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.SharedKernel.Memory;

namespace Harness.Modules.Governance.Memory;

/// <summary>
/// Documento empacotado no bundle de contexto do servidor (Fase 6).
/// </summary>
public sealed record ContextBundleDocument(
    string DocumentId,
    string Title,
    string Content,
    string CitationReference,
    int TokenCount);

/// <summary>
/// Snapshot auditável de contexto empacotado no servidor (Fase 6).
/// </summary>
public sealed record ContextSnapshot(
    string SnapshotId,
    string TenantId,
    string TaskId,
    string ManifestChecksum,
    IReadOnlyList<ContextBundleDocument> Bundles,
    int TotalTokens,
    string Hash,
    DateTimeOffset CreatedAt);

/// <summary>
/// Resultado da busca híbrida RAG com pontuação RRF e citações.
/// </summary>
public sealed record HybridSearchResult(
    VectorDocumentRecord Document,
    double RrfScore,
    string RankExplanation);

public sealed record RagContextSlice(
    string DocumentId,
    string DocumentType,
    string ProjectId,
    string Content,
    double Score,
    string CitationReference,
    IReadOnlyDictionary<string, string> Metadata,
    int TokenCount);

/// <summary>
/// Contrato da engine de busca híbrida (Frees FTS + Vetores + Rerank) (Fase 6 / N6).
/// </summary>
public interface IHybridRagSearchEngine
{
    IReadOnlyList<HybridSearchResult> Search(
        string tenantId,
        string query,
        IReadOnlyList<float>? queryEmbedding,
        IReadOnlyList<VectorDocumentRecord> corpus,
        int topK = 5,
        string? projectId = null);
}

/// <summary>
/// Implementação da engine de busca híbrida (FTS + Vetores + Rerank) (Fase 6 / N6).
/// </summary>
public sealed class HybridRagSearchEngine : IHybridRagSearchEngine
{
    public IReadOnlyList<HybridSearchResult> Search(
        string tenantId,
        string query,
        IReadOnlyList<float>? queryEmbedding,
        IReadOnlyList<VectorDocumentRecord> corpus,
        int topK = 5,
        string? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(corpus);

        var tenantCorpus = corpus
            .Where(document =>
                string.Equals(document.TenantId, tenantId, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(projectId) ||
                    string.Equals(document.ProjectId, projectId, StringComparison.Ordinal)))
            .ToList();

        if (tenantCorpus.Count == 0)
        {
            return [];
        }

        // Rank FTS por palavras-chave na query
        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ftsRanked = tenantCorpus
            .Select(doc =>
            {
                var matches = queryTerms.Count(term => doc.Content.Contains(term, StringComparison.OrdinalIgnoreCase));
                return (Doc: doc, FtsScore: matches);
            })
            .OrderByDescending(x => x.FtsScore)
            .ToList();

        // Rank vetorial
        var vectorRanked = new List<(VectorDocumentRecord Doc, double Score)>();
        if (queryEmbedding is not null && queryEmbedding.Count > 0)
        {
            foreach (var doc in tenantCorpus)
            {
                var score = ComputeCosineSimilarity(queryEmbedding, doc.Embedding);
                vectorRanked.Add((doc, score));
            }
            vectorRanked = vectorRanked.OrderByDescending(x => x.Score).ToList();
        }

        // Reciprocal Rank Fusion (RRF)
        var scoreDict = new Dictionary<string, (VectorDocumentRecord Doc, double Score, string Explanation)>();

        for (int i = 0; i < ftsRanked.Count; i++)
        {
            var doc = ftsRanked[i].Doc;
            var rankFts = i + 1;
            var rrfFts = 0.5 / (60.0 + rankFts);

            scoreDict[doc.Id] = (doc, rrfFts, $"fts_rank:{rankFts}");
        }

        if (vectorRanked.Count > 0)
        {
            for (int i = 0; i < vectorRanked.Count; i++)
            {
                var doc = vectorRanked[i].Doc;
                var rankVec = i + 1;
                var rrfVec = 0.5 / (60.0 + rankVec);

                if (scoreDict.TryGetValue(doc.Id, out var existing))
                {
                    scoreDict[doc.Id] = (doc, existing.Score + rrfVec, $"{existing.Explanation}+vector_rank:{rankVec}");
                }
                else
                {
                    scoreDict[doc.Id] = (doc, rrfVec, $"vector_rank:{rankVec}");
                }
            }
        }

        return scoreDict.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new HybridSearchResult(x.Doc, Math.Round(x.Score, 6), x.Explanation))
            .ToList();
    }

    private static double ComputeCosineSimilarity(IReadOnlyList<float> v1, IReadOnlyList<float> v2)
    {
        if (v1.Count == 0 || v2.Count == 0 || v1.Count != v2.Count)
        {
            return 0.0;
        }

        double dot = 0.0, norm1 = 0.0, norm2 = 0.0;
        for (int i = 0; i < v1.Count; i++)
        {
            dot += v1[i] * v2[i];
            norm1 += v1[i] * v1[i];
            norm2 += v2[i] * v2[i];
        }

        if (norm1 <= 0.0 || norm2 <= 0.0) return 0.0;
        return Math.Clamp(dot / (Math.Sqrt(norm1) * Math.Sqrt(norm2)), 0.0, 1.0);
    }
}

public interface IRagContextProvider
{
    Task<IReadOnlyList<RagContextSlice>> SearchAsync(
        string tenantId,
        string? projectId,
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Porta produtiva da memória semântica. O corpus é restringido por tenant/projeto no store
/// antes do ranking e do topK; a engine aplica FTS + vetores + RRF sobre esse corpus já isolado.
/// </summary>
public sealed class RagContextProvider(
    IVectorIndex vectorIndex,
    IHybridRagSearchEngine searchEngine) : IRagContextProvider
{
    private readonly IVectorIndex _vectorIndex =
        vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
    private readonly IHybridRagSearchEngine _searchEngine =
        searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));

    public async Task<IReadOnlyList<RagContextSlice>> SearchAsync(
        string tenantId,
        string? projectId,
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (topK is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(topK));
        }

        var corpus = await _vectorIndex.ListAsync(tenantId, projectId, cancellationToken);
        var results = _searchEngine.Search(
            tenantId,
            query,
            DeterministicLocalEmbedding.Embed(query),
            corpus,
            topK,
            projectId);

        return results.Select(result => new RagContextSlice(
            result.Document.Id,
            result.Document.DocumentType,
            result.Document.ProjectId,
            result.Document.Content,
            result.RrfScore,
            $"{result.Document.DocumentType}:{result.Document.Id}",
            result.Document.Metadata,
            Math.Max(1, result.Document.Content.Length / 4))).ToArray();
    }
}

/// <summary>
/// Contrato do construtor de pacotes de contexto server-side (Fase 6 / N6).
/// </summary>
public interface IContextBuilder
{
    ContextSnapshot BuildSnapshot(
        string tenantId,
        string taskId,
        string manifestChecksum,
        IReadOnlyList<ContextBundleDocument> requestedBundles,
        int maxTokens = 8000,
        DateTimeOffset? now = null);
}

/// <summary>
/// Construtor de pacotes de contexto server-side com limite de orçamento de tokens e hash auditável (Fase 6 / N6).
/// </summary>
public sealed class ContextBuilder : IContextBuilder
{
    public ContextSnapshot BuildSnapshot(
        string tenantId,
        string taskId,
        string manifestChecksum,
        IReadOnlyList<ContextBundleDocument> requestedBundles,
        int maxTokens = 8000,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(manifestChecksum);
        ArgumentNullException.ThrowIfNull(requestedBundles);

        var timestamp = now ?? DateTimeOffset.UtcNow;
        var acceptedBundles = new List<ContextBundleDocument>();
        var currentTokens = 0;

        foreach (var doc in requestedBundles)
        {
            if (currentTokens + doc.TokenCount <= maxTokens)
            {
                acceptedBundles.Add(doc);
                currentTokens += doc.TokenCount;
            }
            else
            {
                // Truncamento seguro com preservação da integridade contratual
                var remainingBudget = maxTokens - currentTokens;
                if (remainingBudget > 50)
                {
                    var truncatedContent = doc.Content.Length > remainingBudget * 4
                        ? doc.Content[..(remainingBudget * 4)] + "..."
                        : doc.Content;

                    acceptedBundles.Add(doc with
                    {
                        Content = truncatedContent,
                        TokenCount = remainingBudget
                    });
                    currentTokens += remainingBudget;
                }
                break;
            }
        }

        var snapshotId = Guid.NewGuid().ToString("N");
        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(acceptedBundles));
        var hashHex = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();

        return new ContextSnapshot(
            SnapshotId: snapshotId,
            TenantId: tenantId,
            TaskId: taskId,
            ManifestChecksum: manifestChecksum,
            Bundles: acceptedBundles,
            TotalTokens: currentTokens,
            Hash: hashHex,
            CreatedAt: timestamp);
    }
}
