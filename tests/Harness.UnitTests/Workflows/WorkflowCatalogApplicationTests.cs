using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Contracts;

namespace Harness.UnitTests.Workflows;

public sealed class WorkflowCatalogApplicationTests
{
    [Fact]
    public void DraftTemplateAcceptsTheMinimalFr4Input()
    {
        var value = WorkflowCatalogApplicationService.CreateDraftTemplate(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            new CreateWorkflowTemplateRequest("  Entrega  "),
            new DateTimeOffset(2026, 7, 19, 21, 0, 0, TimeSpan.Zero));

        Assert.Equal("Entrega", value.Name);
        Assert.Empty(value.Description);
        Assert.Throws<ArgumentException>(() => WorkflowCatalogApplicationService.CreateDraftTemplate(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            new CreateWorkflowTemplateRequest(" "),
            value.OccurredAt));
    }

    [Fact]
    public void DraftVersionMayBeIncompleteWithoutPublishing()
    {
        var value = WorkflowCatalogApplicationService.CreateDraftVersion(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new WorkflowDraftRequest(),
            new DateTimeOffset(2026, 7, 19, 21, 1, 0, TimeSpan.Zero));

        Assert.Empty(value.Hierarchy.Phases);
        Assert.Empty(value.PhaseConfigs);
        Assert.Null(value.DefaultOperationMode);
    }

    [Fact]
    public void FrontendPhaseShapeExpandsIntoValidatedAuthorityHierarchy()
    {
        var value = WorkflowCatalogApplicationService.CreateTemplate(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new CreateWorkflowTemplateRequest("Entrega", "Descrição", ["Planejar", "Validar"],
                new Dictionary<string, IReadOnlyList<string>> { { "Validar", ["Qualidade"] } }),
            new DateTimeOffset(2026, 7, 18, 20, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, value.Phases.Count); Assert.Empty(value.Phases[0].Gates);
        var validation = value.Phases[1]; Assert.Equal(2, validation.Objectives.Count); var gate = Assert.Single(validation.Gates);
        Assert.Equal("validated", gate.MinimumRequiredState); Assert.Single(gate.RequiredObjectiveIds);
        Assert.Throws<ArgumentException>(() => WorkflowCatalogApplicationService.CreateBinding(
            new CreateWorkflowRequest("01ARZ3NDEKTSV4RRFFQ69G5FAX", "01ARZ3NDEKTSV4RRFFQ69G5FAY", null, "autonomous", ["Release"], "aceite")));
    }

    [Fact]
    public void VersionAndRuntimeCommandsEnforceWorkflowInvariants()
    {
        var agentId = "01ARZ3NDEKTSV4RRFFQ69G5FAZ";
        var value = WorkflowCatalogApplicationService.CreateVersion(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new PublishWorkflowVersionRequest(
                ["Planejar", "Entregar"],
                new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, WorkflowPhaseConfigContract>
                {
                    ["Planejar"] = new(["brief"], 40m, [agentId]),
                },
                "semiautonomous",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["Planejar"] = ["Entregar"],
                },
                "v2"),
            new DateTimeOffset(2026, 7, 18, 21, 0, 0, TimeSpan.Zero));

        Assert.Equal("semiautonomous", value.DefaultOperationMode);
        Assert.Equal(40m, value.PhaseConfigs["Planejar"].ProgressWeight);
        Assert.Equal(["Entregar"], value.Transitions["Planejar"]);
        Assert.Equal("pause", WorkflowCatalogApplicationService.RunTransition(
            new TransitionWorkflowRunRequest("pause")));
        Assert.Equal(("Planejar", "work-1", "approved"),
            WorkflowCatalogApplicationService.AdvanceObjective(
                new AdvanceWorkflowObjectiveRequest("Planejar", "work-1", "approved")));

        Assert.Throws<ArgumentException>(() => WorkflowCatalogApplicationService.EvaluateGate(
            new EvaluateWorkflowGateRequest("Entregar", "gate-1", false)));
        Assert.Throws<ArgumentException>(() => WorkflowCatalogApplicationService.CreateVersion(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            new PublishWorkflowVersionRequest(
                ["Planejar"],
                new Dictionary<string, IReadOnlyList<string>>(),
                Transitions: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["Planejar"] = ["Ausente"],
                }),
            new DateTimeOffset(2026, 7, 18, 21, 0, 0, TimeSpan.Zero)));
    }
}
