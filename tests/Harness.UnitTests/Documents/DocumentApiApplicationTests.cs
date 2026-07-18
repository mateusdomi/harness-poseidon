using Harness.Modules.Documents.Application;
using Harness.Modules.Documents.Contracts;

namespace Harness.UnitTests.Documents;

public sealed class DocumentApiApplicationTests
{
    [Fact]
    public void ContentAndMetadataAreCanonical()
    {
        var value = DocumentApiApplicationService.Prepare(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX", "# Spec\n\nConteúdo.");
        Assert.Equal(64, value.ContentHash.Length);
        Assert.Equal(
            "documents/01ARZ3NDEKTSV4RRFFQ69G5FAV/01ARZ3NDEKTSV4RRFFQ69G5FAW/01ARZ3NDEKTSV4RRFFQ69G5FAX.md",
            value.CatalogPath);

        var normalized = DocumentApiApplicationService.Normalize(new CreateDocumentRequest(
            "01ARZ3NDEKTSV4RRFFQ69G5FAY", "  ADR 001  ", "spec", "body",
            ["ux", "arquitetura", "ux"], "  Planejamento  "));
        Assert.Equal("ADR 001", normalized.Title);
        Assert.Equal(["arquitetura", "ux"], normalized.Classifications);
        Assert.Equal("Planejamento", normalized.PhaseName);
        Assert.Equal("awaitingApproval", DocumentApiApplicationService.ToApiState("awaiting_approval"));
        Assert.Equal(("approved", "Aceito."), DocumentApiApplicationService.Resolution(
            new ResolveApprovalRequest("approved", " Aceito. ")));
        Assert.Throws<ArgumentException>(() => DocumentApiApplicationService.Resolution(
            new ResolveApprovalRequest("rejected")));
    }
}
