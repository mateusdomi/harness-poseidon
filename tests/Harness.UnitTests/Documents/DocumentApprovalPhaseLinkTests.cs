using Harness.Host.Documents;
using Harness.Persistence.Abstractions.Workflows;

namespace Harness.UnitTests.Documents;

/// <summary>
/// A regra de casamento documento↔objetivo da etapa. É pura de propósito: quem
/// decide que a aprovação do dono vale para ESTE artefato precisa ser auditável
/// sem banco, e nenhum empate pode ser resolvido por chute.
/// </summary>
public sealed class DocumentApprovalPhaseLinkTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public void MatchesDocumentObjectiveByNameInsideTheDocumentPhase()
    {
        var aggregate = Aggregate(
            Phase("phase-1", "Triagem", "completed", Objective("document-1", "Briefing", "document", "approved")),
            Phase("phase-2", "Análise", "active", Objective("document-2", "Especificação", "document", "validated")));

        var match = DocumentApprovalPhaseLink.Match(aggregate, "Análise", "Especificação");

        Assert.NotNull(match);
        Assert.Equal("phase-2", match.PhaseKey);
        Assert.Equal("document-2", match.ObjectiveKey);
        Assert.Equal("validated", match.ObjectiveState);
    }

    [Fact]
    public void WithoutPhaseOnTheDocumentUsesTheActivePhase()
    {
        var aggregate = Aggregate(
            Phase("phase-1", "Triagem", "completed", Objective("document-1", "Briefing", "document", "approved")),
            Phase("phase-2", "Análise", "active", Objective("document-2", "Briefing", "document", "validated")));

        var match = DocumentApprovalPhaseLink.Match(aggregate, null, "Briefing");

        Assert.NotNull(match);
        Assert.Equal("phase-2", match.PhaseKey);
    }

    [Fact]
    public void IgnoresObjectivesThatAreNotDocuments()
    {
        var aggregate = Aggregate(
            Phase("phase-1", "Análise", "active", Objective("work-1", "Especificação", "task", "validated")));

        Assert.Null(DocumentApprovalPhaseLink.Match(aggregate, "Análise", "Especificação"));
        Assert.False(DocumentApprovalPhaseLink.IsAmbiguous(aggregate, "Análise", "Especificação"));
    }

    [Fact]
    public void RepeatedArtifactNameIsDeclaredAmbiguityNotAGuess()
    {
        var aggregate = Aggregate(
            Phase(
                "phase-1", "Análise", "active",
                Objective("document-1", "Especificação", "document", "validated"),
                Objective("document-2", "Especificação", "document", "pending")));

        Assert.Null(DocumentApprovalPhaseLink.Match(aggregate, "Análise", "Especificação"));
        Assert.True(DocumentApprovalPhaseLink.IsAmbiguous(aggregate, "Análise", "Especificação"));
    }

    [Fact]
    public void MatchToleratesSpacingAndCase()
    {
        var aggregate = Aggregate(
            Phase("phase-1", " Análise ", "active", Objective("document-1", " Especificação ", "document", "validated")));

        Assert.NotNull(DocumentApprovalPhaseLink.Match(aggregate, "análise", "ESPECIFICAÇÃO"));
    }

    [Fact]
    public void UnknownPhaseNameMatchesNothing()
    {
        var aggregate = Aggregate(
            Phase("phase-1", "Análise", "active", Objective("document-1", "Especificação", "document", "validated")));

        Assert.Null(DocumentApprovalPhaseLink.Match(aggregate, "Etapa que não existe", "Especificação"));
    }

    private static WorkflowRunAggregateSnapshot Aggregate(params WorkflowPhaseRunSnapshot[] phases) =>
        new(
            TenantId,
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            "01ARZ3NDEKTSV4RRFFQ69G5FAZ",
            "running",
            7,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            0m,
            0m,
            0m,
            phases);

    private static WorkflowPhaseRunSnapshot Phase(
        string key, string name, string state, params WorkflowObjectiveRunSnapshot[] objectives) =>
        new(
            $"run-{key}",
            $"definition-{key}",
            key,
            name,
            1,
            state,
            1,
            DateTimeOffset.UnixEpoch,
            null,
            objectives,
            []);

    private static WorkflowObjectiveRunSnapshot Objective(
        string key, string name, string kind, string state) =>
        new($"run-{key}", $"definition-{key}", key, name, kind, 1m, state, 1, DateTimeOffset.UnixEpoch);
}
