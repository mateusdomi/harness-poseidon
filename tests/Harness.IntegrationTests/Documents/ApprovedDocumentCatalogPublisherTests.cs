using System.Security.Cryptography;
using System.Text;
using Harness.Host.Documents;

namespace Harness.IntegrationTests.Documents;

public sealed class ApprovedDocumentCatalogPublisherTests
{
    [Fact]
    public void ADocumentCardMustProduceOneCanonicalMarkdownArtifact()
    {
        var selected = ApprovedDocumentCatalogPublisher.SelectDocumentArtifacts(
        [
            "src/Service.cs",
            "docs/triagem/DOC-01.md",
            "docs/triagem/notes.txt",
            "docs/triagem/DOC-01.md",
        ]);

        Assert.Equal(["docs/triagem/DOC-01.md"], selected);
    }

    [Fact]
    public void ThePlaybookTemplateAndVisibleTitleComeFromTheDeliveredPackage()
    {
        Assert.Equal(
            "01",
            ApprovedDocumentCatalogPublisher.ParseTemplateCode(
                "# Estrutura\n- Template: 01 — Ficha de Demanda Qualificada\n"));
        Assert.Equal(
            "Ficha de Demanda Qualificada",
            ApprovedDocumentCatalogPublisher.ExtractTitle(
                "# Ficha de Demanda Qualificada\n\nConteúdo.", "fallback"));
        Assert.Equal(
            "prd",
            ApprovedDocumentCatalogPublisher.InferKind(
                "1-Triagem", "01", "docs/triagem/DOC-01.md"));
    }

    [Fact]
    public async Task CatalogContentWriteIsIdempotentOnlyForTheSameHash()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory, "document-content-idempotency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new FileSystemDocumentContentCatalog(root);
            const string path = "documents/tenant/document/version.md";
            const string body = "# Documento\n";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

            await catalog.WriteAsync(path, body, hash);
            await catalog.WriteAsync(path, body, hash);

            var otherHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("conteúdo diferente")));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => catalog.WriteAsync(path, "conteúdo diferente", otherHash));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
