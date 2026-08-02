using Harness.Modules.Governance.Memory;
using Harness.SharedKernel.Memory;

namespace Harness.UnitTests.Governance;

public sealed class SemanticMemoryAndContextBuilderTests
{
    [Fact]
    public void HybridRagSearchEngineCombinesFtsAndVectorWithRrf()
    {
        var engine = new HybridRagSearchEngine();
        var tenantId = "tenant-1";

        var doc1 = new VectorDocumentRecord(
            "doc-1", tenantId, "proj-1", "architecture",
            "Postgres vector index memory RAG system",
            [1.0f, 0.0f, 0.0f],
            new Dictionary<string, string> { { "author", "architect" } },
            DateTimeOffset.UtcNow);

        var doc2 = new VectorDocumentRecord(
            "doc-2", tenantId, "proj-1", "workflow",
            "Workflow orchestration and execution state engine",
            [0.0f, 1.0f, 0.0f],
            new Dictionary<string, string> { { "author", "engineer" } },
            DateTimeOffset.UtcNow);

        var corpus = new[] { doc1, doc2 };

        var results = engine.Search(
            tenantId: tenantId,
            query: "vector index RAG",
            queryEmbedding: [0.9f, 0.1f, 0.0f],
            corpus: corpus,
            topK: 2);

        Assert.NotEmpty(results);
        Assert.Equal("doc-1", results[0].Document.Id);
        Assert.True(results[0].RrfScore > 0);
    }

    [Fact]
    public void HybridRagSearchEngineScopesProjectBeforeTopK()
    {
        var engine = new HybridRagSearchEngine();
        var tenantId = "tenant-1";
        var corpus = new[]
        {
            new VectorDocumentRecord(
                "other-project", tenantId, "proj-2", "attachment",
                "exportar auditoria csv filtro data",
                [1.0f, 0.0f],
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow),
            new VectorDocumentRecord(
                "requested-project", tenantId, "proj-1", "attachment",
                "auditoria csv",
                [0.8f, 0.2f],
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow),
        };

        var result = Assert.Single(engine.Search(
            tenantId,
            "exportar auditoria csv filtro data",
            [1.0f, 0.0f],
            corpus,
            topK: 1,
            projectId: "proj-1"));

        Assert.Equal("requested-project", result.Document.Id);
    }

    [Fact]
    public void ContextBuilderEnforcesTokenBudgetAndComputesAuditHash()
    {
        var builder = new ContextBuilder();
        var requested = new List<ContextBundleDocument>
        {
            new("doc-1", "Architecture ADR", "This is ADR 001 for microservices.", "ADR-001#L1-10", 100),
            new("doc-2", "Security Policy", "This is security PEP definition.", "SEC-002#L15-30", 150)
        };

        var snapshot = builder.BuildSnapshot(
            tenantId: "tenant-1",
            taskId: "task-100",
            manifestChecksum: "sha256:abc123456",
            requestedBundles: requested,
            maxTokens: 200);

        Assert.NotNull(snapshot);
        Assert.Equal("tenant-1", snapshot.TenantId);
        Assert.Equal("task-100", snapshot.TaskId);
        Assert.Equal(200, snapshot.TotalTokens);
        Assert.NotEmpty(snapshot.Hash);
        Assert.Equal(2, snapshot.Bundles.Count);
        Assert.Equal("doc-1", snapshot.Bundles[0].DocumentId);
    }

    [Fact]
    public async Task RagProviderKeepsEveryExplicitAttachmentBeyondSemanticTopK()
    {
        var tenantId = "tenant-1";
        var projectId = "proj-1";
        var documents = Enumerable.Range(1, 6)
            .Select(index => new VectorDocumentRecord(
                $"attachment-{index}",
                tenantId,
                projectId,
                "solicitation_attachment",
                $"fonte-{index}.txt: conteúdo {index}",
                DeterministicLocalEmbedding.Embed($"conteúdo {index}"),
                new Dictionary<string, string> { ["fileName"] = $"fonte-{index}.txt" },
                DateTimeOffset.UtcNow.AddSeconds(index)))
            .Append(new VectorDocumentRecord(
                "other-project",
                tenantId,
                "proj-2",
                "solicitation_attachment",
                "fonte-externa.txt: segredo",
                DeterministicLocalEmbedding.Embed("segredo"),
                new Dictionary<string, string> { ["fileName"] = "fonte-externa.txt" },
                DateTimeOffset.UtcNow))
            .Append(new VectorDocumentRecord(
                "attachment-1-obsolete",
                tenantId,
                projectId,
                "solicitation_attachment",
                "fonte-1.txt: conteúdo obsoleto",
                DeterministicLocalEmbedding.Embed("conteúdo obsoleto"),
                new Dictionary<string, string> { ["fileName"] = "fonte-1.txt" },
                DateTimeOffset.UtcNow.AddDays(-1)))
            .ToArray();
        var provider = new RagContextProvider(
            new StubVectorIndex(documents),
            new HybridRagSearchEngine());

        var result = await provider.SearchAsync(
            tenantId,
            projectId,
            "Fontes fornecidas: fonte-1.txt fonte-2.txt fonte-3.txt fonte-4.txt " +
            "fonte-5.txt fonte-6.txt fonte-externa.txt",
            topK: 5);

        Assert.Equal(6, result.Count);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(index => $"attachment-{index}"),
            result.Select(slice => slice.DocumentId));
        Assert.DoesNotContain(result, slice => slice.DocumentId == "other-project");
    }

    private sealed class StubVectorIndex(IReadOnlyList<VectorDocumentRecord> documents)
        : IVectorIndex
    {
        public Task IndexAsync(
            VectorDocumentRecord document,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorDocumentRecord>> ListAsync(
            string tenantId,
            string? projectId = null,
            CancellationToken cancellationToken = default) => Task.FromResult(documents);

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            string tenantId,
            IReadOnlyList<float> queryEmbedding,
            int topK = 10,
            double minScore = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);

        public Task DeleteAsync(
            string tenantId,
            string documentId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
