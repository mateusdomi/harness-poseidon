using Harness.Modules.Agents.Application.Execution;
using Harness.Persistence.Abstractions.Documents;

namespace Harness.Host.Documents;

/// <summary>
/// Makes project source documents stored in the canonical document catalog visible to Bruna's
/// conversational context. V3 treats uploads from Chat, Documents and Prototypes as project input;
/// the Chief prompt cannot depend only on legacy solicitation attachments.
/// </summary>
public sealed class DocumentCatalogChiefNavigator(
    IDocumentCatalogStore documents,
    IDocumentContentCatalog content) : IChiefAttachmentNavigator
{
    private readonly IDocumentCatalogStore _documents =
        documents ?? throw new ArgumentNullException(nameof(documents));
    private readonly IDocumentContentCatalog _content =
        content ?? throw new ArgumentNullException(nameof(content));

    public async Task<IReadOnlyList<ChiefAttachmentOutline>> ListOutlinesAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var outlines = new List<ChiefAttachmentOutline>();
        foreach (var document in await ListSourceDocumentsAsync(tenantId, projectId, cancellationToken))
        {
            var text = await ReadCurrentVersionAsync(tenantId, document, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var sections = AttachmentSectionizer.Split(text);
            outlines.Add(new ChiefAttachmentOutline(
                document.Title,
                sections.Select(section => new ChiefAttachmentSectionRef(section.Id, section.Title)).ToArray()));
        }

        return outlines;
    }

    public async Task<string?> ReadSectionAsync(
        string tenantId,
        string projectId,
        string fileName,
        string sectionId,
        CancellationToken cancellationToken = default)
    {
        foreach (var document in await ListSourceDocumentsAsync(tenantId, projectId, cancellationToken))
        {
            if (!string.Equals(document.Title, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = await ReadCurrentVersionAsync(tenantId, document, cancellationToken);
            if (text is null)
            {
                continue;
            }

            return AttachmentSectionizer.Find(AttachmentSectionizer.Split(text), sectionId)?.Content;
        }

        return null;
    }

    public async Task<IReadOnlyList<ChiefPrimaryRequirementSource>> ListPrimaryRequirementSourcesAsync(
        string tenantId,
        string projectId,
        int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        if (maximumCharacters <= 0)
        {
            return [];
        }

        var sources = new List<ChiefPrimaryRequirementSource>();
        var remaining = maximumCharacters;
        foreach (var document in await ListSourceDocumentsAsync(tenantId, projectId, cancellationToken))
        {
            var role = DocumentSourceRole(document);
            if (!string.Equals(role, "requirements_source", StringComparison.Ordinal))
            {
                continue;
            }

            var text = await ReadCurrentVersionAsync(tenantId, document, cancellationToken);
            if (string.IsNullOrWhiteSpace(text) || text.Length > remaining)
            {
                continue;
            }

            var sections = AttachmentSectionizer.Split(text);
            sources.Add(new ChiefPrimaryRequirementSource(
                document.Title,
                role!,
                sections.Count,
                sections.Count,
                text.Length,
                text));
            remaining -= text.Length;
        }

        return sources;
    }

    private async Task<IReadOnlyList<DocumentCatalogRecord>> ListSourceDocumentsAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var rows = await _documents.ListDocumentsAsync(tenantId, projectId, null, 200, cancellationToken);
        return rows
            .Where(document => DocumentSourceRole(document) is not null)
            .OrderByDescending(document => document.UpdatedAt)
            .ToArray();
    }

    private async Task<string?> ReadCurrentVersionAsync(
        string tenantId,
        DocumentCatalogRecord document,
        CancellationToken cancellationToken)
    {
        var versions = await _documents.ListVersionsAsync(tenantId, document.Id, null, 200, cancellationToken);
        var version = versions.FirstOrDefault(item => item.Version == document.CurrentVersion) ??
            versions.OrderByDescending(item => item.Version).FirstOrDefault();
        if (version is null)
        {
            return null;
        }

        try
        {
            return await _content.ReadAsync(version.CatalogPath, version.ContentHash, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static string? DocumentSourceRole(DocumentCatalogRecord document)
    {
        var tokens = $"{document.Title} {document.Kind} {string.Join(' ', document.Classifications)}"
            .ToLowerInvariant();
        if (tokens.Contains("prototype", StringComparison.Ordinal) ||
            tokens.Contains("protótipo", StringComparison.Ordinal) ||
            tokens.Contains("referência", StringComparison.Ordinal) ||
            tokens.Contains("reference", StringComparison.Ordinal) ||
            string.Equals(document.Kind, "design", StringComparison.OrdinalIgnoreCase))
        {
            return "design_reference";
        }

        if (string.Equals(document.Kind, "prd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.Kind, "spec", StringComparison.OrdinalIgnoreCase) ||
            tokens.Contains("requirement", StringComparison.Ordinal) ||
            tokens.Contains("requis", StringComparison.Ordinal) ||
            tokens.Contains("especifica", StringComparison.Ordinal) ||
            tokens.Contains("source", StringComparison.Ordinal) ||
            tokens.Contains("fonte", StringComparison.Ordinal) ||
            tokens.Contains("primary", StringComparison.Ordinal))
        {
            return "requirements_source";
        }

        return null;
    }
}
