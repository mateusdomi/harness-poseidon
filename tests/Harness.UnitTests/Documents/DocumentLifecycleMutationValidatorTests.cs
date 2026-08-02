using Harness.Persistence.Abstractions.Documents;

namespace Harness.UnitTests.Documents;

public sealed class DocumentLifecycleMutationValidatorTests
{
    [Fact]
    public void ReopeningAnApprovedDocumentIsReservedToTheGovernedRevisionPath()
    {
        // Um documento aprovado é imutável. Sem uma reabertura explícita, revisar um artefato já
        // aceito só poderia ser feito gravando a versão nova por baixo do estado `approved` — e
        // ela herdaria a aprovação da anterior sem que ninguém a tivesse revisado. Reabrir devolve
        // o documento à elaboração, onde a versão nova conquista a própria aprovação.
        Assert.True(DocumentLifecycleMutationValidator.CanTransition(
            "approved", "in_elaboration", "system"));

        // Restrito ao sistema: se a API ou um humano pudesse reabrir, a imutabilidade viraria
        // apenas um passo a mais para contornar.
        Assert.False(DocumentLifecycleMutationValidator.CanTransition(
            "approved", "in_elaboration", "user"));
        Assert.False(DocumentLifecycleMutationValidator.CanTransition(
            "approved", "in_elaboration", "agent"));

        // Nada além disso muda: aprovado continua sem caminho de volta para revisão direta.
        Assert.False(DocumentLifecycleMutationValidator.CanTransition(
            "approved", "in_review", "system"));
    }

    [Fact]
    public void OnlySystemEvidenceCanProjectAnIndependentlyReviewedCardAsApproved()
    {
        Assert.True(DocumentLifecycleMutationValidator.CanTransition(
            "in_elaboration", "approved", "system"));
        Assert.False(DocumentLifecycleMutationValidator.CanTransition(
            "in_elaboration", "approved", "user"));
        Assert.False(DocumentLifecycleMutationValidator.CanTransition(
            "in_elaboration", "approved", "agent"));

        var command = Command("approved-card:01ARZ3NDEKTSV4RRFFQ69G5FA;" +
                              "review-attempt:01ARZ3NDEKTSV4RRFFQ69G5FB");
        DocumentLifecycleMutationValidator.Validate(command);
        Assert.Throws<ArgumentException>(() =>
            DocumentLifecycleMutationValidator.Validate(Command("sem evidência rastreável")));
    }

    private static DocumentTransitionCommand Command(string note) => new(
        "01ARZ3NDEKTSV4RRFFQ69G5FZK",
        "01ARZ3NDEKTSV4RRFFQ69G5FZJ",
        "01ARZ3NDEKTSV4RRFFQ69G5FZH",
        "approved",
        note,
        "system",
        null,
        1,
        "approved-document-review:test",
        new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero));
}
