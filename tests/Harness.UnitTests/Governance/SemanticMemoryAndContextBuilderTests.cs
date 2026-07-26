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
}
