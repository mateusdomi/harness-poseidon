using Harness.Persistence.Abstractions.Documents;
using Harness.SharedKernel.Identifiers;

namespace Harness.UnitTests.Documents;

/// <summary>
/// Guardrail anti-proliferação de documentos. Prova, no ponto de estrangulamento comum a todos os
/// stores (<see cref="DocumentCreateValidator"/>): criação programática (chief/agent) SEM origem
/// legítima é recusada; com origem válida é aceita; criação interativa por humano ('user') não
/// exige origem, mas uma origem malformada ainda é rejeitada.
/// </summary>
public sealed class DocumentOriginGuardrailTests
{
    private static string Ulid() => UlidValue.New(DateTimeOffset.UtcNow).ToString();

    private static DocumentCreateCommand Command(string authorKind, string? originReference) =>
        new(
            TenantId: Ulid(),
            ProjectId: Ulid(),
            DocumentId: Ulid(),
            Title: "ADR 001",
            Kind: "spec",
            Classifications: [],
            PhaseName: null,
            DocumentVersionId: Ulid(),
            CatalogPath: "documents/tenant/doc/version.md",
            ContentHash: new string('A', 64),
            AuthorKind: authorKind,
            AuthorId: Ulid(),
            IdempotencyKey: "api:document:create:test",
            OccurredAt: DateTimeOffset.UtcNow,
            OriginReference: originReference);

    [Theory]
    [InlineData("chief")]
    [InlineData("agent")]
    public void ProgrammaticCreationWithoutAnOriginIsRefused(string authorKind)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DocumentCreateValidator.Validate(Command(authorKind, originReference: null)));
        Assert.Contains("origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("chief")]
    [InlineData("agent")]
    public void ProgrammaticCreationWithAValidCardOriginIsAccepted(string authorKind)
    {
        // Não lança: a origem tipada (ULID do card/fluxo) satisfaz o guardrail.
        DocumentCreateValidator.Validate(Command(authorKind, originReference: Ulid()));
    }

    [Fact]
    public void InteractiveHumanCreationDoesNotRequireAnOrigin()
    {
        // A criação por um humano na própria sessão É um fluxo legítimo; a origem é opcional.
        DocumentCreateValidator.Validate(Command("user", originReference: null));
    }

    [Fact]
    public void AMalformedOriginIsAlwaysRejected()
    {
        Assert.Throws<ArgumentException>(
            () => DocumentCreateValidator.Validate(Command("user", originReference: "not-a-ulid")));
    }
}
