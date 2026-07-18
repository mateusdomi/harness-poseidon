using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Contracts;

namespace Harness.UnitTests.Workflows;

public sealed class WorkflowCatalogApplicationTests
{
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
}
