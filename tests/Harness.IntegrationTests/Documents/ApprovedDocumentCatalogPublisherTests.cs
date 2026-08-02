using System.Security.Cryptography;
using System.Text;
using Harness.Host.Documents;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;

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
    public void AnApprovedCardPublishesItsLatestOperationallyCompletedAttempt()
    {
        var started = new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero);
        var failed = Attempt("01ARZ3NDEKTSV4RRFFQ69G5FA", "failed", started);
        var delivered = Attempt("01ARZ3NDEKTSV4RRFFQ69G5FB", "completed", started.AddMinutes(1));

        var selected = ApprovedDocumentCatalogPublisher.SelectDeliveredAttempt([failed, delivered]);

        Assert.Equal(delivered.Id, selected?.Id);
        Assert.Null(ApprovedDocumentCatalogPublisher.SelectDeliveredAttempt([failed]));
    }

    [Fact]
    public void CatalogApprovalRequiresTheDurableIndependentReviewForThePublishedAttempt()
    {
        var at = new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero);
        var approved = Aggregate(
            new WorkReviewSnapshot("review-1", "reviewer", "approved", "Aprovado.", at));

        Assert.Equal(
            "review-1",
            ApprovedDocumentCatalogPublisher.SelectApprovedReview(
                approved, "task", "attempt")?.ReviewId);
        Assert.Null(ApprovedDocumentCatalogPublisher.SelectApprovedReview(
            Aggregate(new("review-2", "producer", "approved", "Autorrevisão.", at)),
            "task", "attempt"));
        Assert.Null(ApprovedDocumentCatalogPublisher.SelectApprovedReview(
            Aggregate(new("review-3", "reviewer", "rejected", "Correções.", at)),
            "task", "attempt"));
        Assert.Null(ApprovedDocumentCatalogPublisher.SelectApprovedReview(
            approved, "task", "outra-tentativa"));
    }

    [Fact]
    public void TemplateGateNamesTheMissingSectionsBeforePublication()
    {
        var templates = new[]
        {
            new WorkflowDocumentTemplateRecord(
                "05", "ADR (MADR)", "3-Arquitetura", "adr",
                "[\"contexto\",\"decisao\",\"consequencias_negativas\"]", "{}", "Trade-off."),
        };

        var result = ApprovedDocumentCatalogPublisher.ValidateTemplateContract(
            "- Template: 05 — ADR (MADR)",
            "## Contexto\nConhecido.\n\n## Decisão\nEscolhida.\n",
            templates);

        Assert.False(result.IsValid);
        Assert.Equal("document.template_not_satisfied", result.ReasonCode);
        Assert.Contains("consequencias_negativas", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateGateAcceptsTheCanonicalMadrStrongLabels()
    {
        var templates = new[]
        {
            new WorkflowDocumentTemplateRecord(
                "05", "ADR (MADR)", "3-Arquitetura", "adr",
                "[\"contexto\",\"decisao\",\"consequencias_negativas\"]", "{}", "Trade-off."),
        };

        var result = ApprovedDocumentCatalogPublisher.ValidateTemplateContract(
            "- Template: 05 — ADR (MADR)",
            "**contexto**\nConhecido.\n\n**decisao**\nEscolhida.\n\n**consequencias_negativas**\nCusto.\n",
            templates);

        Assert.True(result.IsValid);
        Assert.Equal("document.template_satisfied", result.ReasonCode);
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

    private static BoardAttemptRecord Attempt(
        string id,
        string operationalState,
        DateTimeOffset startedAt) =>
        new(
            "tenant", id, "task", 1, operationalState, "professional", startedAt,
            startedAt.AddSeconds(1), 1000, 0, 0, 0, [], null, null);

    private static WorkChainAggregateSnapshot Aggregate(WorkReviewSnapshot review)
    {
        var at = new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero);
        return new(
            "tenant", "project", "user", "solicitation", "pedido", at,
            [new WorkDemandSnapshot(
                "demand", "Demanda", [], at,
                [new WorkTaskAggregateSnapshot(
                    "task", "Documento", "low", 1, "approved", 5, at, at,
                    [],
                    [new WorkAttemptSnapshot(
                        "attempt", "instruction", 1, "producer", "approved", at, at,
                        [], review)])])]);
    }
}
