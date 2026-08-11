using Harness.Host.Documents;
using Harness.Persistence.Abstractions.Documents;

namespace Harness.UnitTests.Documents;

public sealed class DocumentCatalogChiefNavigatorTests
{
    [Fact]
    public async Task RequirementsSourceDocumentIsInjectedAsPrimaryRequirement()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var document = new DocumentCatalogRecord(
            "doc-1",
            "project-1",
            "PRIMARY_REQUIREMENTS — Equipment Loans",
            "spec",
            "in_elaboration",
            1,
            ["primary_requirements", "requirements_source", "source"],
            null,
            false,
            now,
            now,
            1);
        var version = new DocumentVersionCatalogRecord(
            "version-1",
            document.Id,
            1,
            "documents/project-1/doc-1/version-1.md",
            "sha256",
            "user",
            "profile-1",
            now);
        var body = """
        # PRIMARY REQUIREMENTS

        ## Scope
        Build the equipment loan system.

        ## Acceptance Criteria
        AC01 — User can log in.
        AC02 — Equipment loans are persisted.
        """;
        var navigator = new DocumentCatalogChiefNavigator(
            new FakeDocumentCatalogStore([document], [version]),
            new FakeDocumentContentCatalog(body));

        var sources = await navigator.ListPrimaryRequirementSourcesAsync("tenant-1", "project-1", 20_000);

        var source = Assert.Single(sources);
        Assert.Equal(document.Title, source.FileName);
        Assert.Equal("requirements_source", source.Role);
        Assert.Equal(source.TotalSections, source.ConsumedSections);
        Assert.Contains("AC01", source.Content, StringComparison.Ordinal);
        Assert.Contains("AC02", source.Content, StringComparison.Ordinal);
    }

    private sealed class FakeDocumentCatalogStore(
        IReadOnlyList<DocumentCatalogRecord> documents,
        IReadOnlyList<DocumentVersionCatalogRecord> versions) : IDocumentCatalogStore
    {
        public Task<IReadOnlyList<DocumentCatalogRecord>> ListDocumentsAsync(
            string tenantId, string? projectId, string? afterId, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(documents.Where(item => item.ProjectId == projectId).ToArray() as IReadOnlyList<DocumentCatalogRecord>);

        public Task<IReadOnlyList<DocumentVersionCatalogRecord>> ListVersionsAsync(
            string tenantId, string? documentId, string? afterId, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(versions.Where(item => item.DocumentId == documentId).ToArray() as IReadOnlyList<DocumentVersionCatalogRecord>);

        public Task<DocumentCatalogPageRecord> PageDocumentsAsync(
            string tenantId, DocumentCatalogPageQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DocumentCatalogRecord?> GetDocumentAsync(
            string tenantId, string documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DocumentVersionCatalogPageRecord> PageVersionsAsync(
            string tenantId, string? documentId, int offset, int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DocumentVersionCatalogRecord?> GetVersionAsync(
            string tenantId, string versionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ApprovalCatalogRecord>> ListApprovalsAsync(
            string tenantId, string? projectId, string? taskId, string? afterId, int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApprovalCatalogPageRecord> PageApprovalsAsync(
            string tenantId, ApprovalCatalogPageQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApprovalCatalogRecord?> GetApprovalAsync(
            string tenantId, string approvalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApprovalCatalogRecord> CreateGeneralApprovalAsync(
            GeneralApprovalCreateCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApprovalCatalogRecord?> ResolveGeneralApprovalAsync(
            GeneralApprovalResolveCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDocumentContentCatalog(string body) : IDocumentContentCatalog
    {
        public Task WriteAsync(string catalogPath, string body, string expectedHash,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ReadAsync(string catalogPath, string expectedHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(body);

        public string ResolveReadPath(string catalogPath, string expectedHash) => catalogPath;

        public Task DeleteAsync(string catalogPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
