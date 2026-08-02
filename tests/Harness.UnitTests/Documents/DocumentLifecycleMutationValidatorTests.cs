using Harness.Persistence.Abstractions.Documents;

namespace Harness.UnitTests.Documents;

public sealed class DocumentLifecycleMutationValidatorTests
{
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
