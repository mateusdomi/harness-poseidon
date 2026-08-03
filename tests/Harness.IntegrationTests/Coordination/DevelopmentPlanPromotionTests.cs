using Harness.Host.WorkBoard;
using Harness.Host.Workflows;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.IntegrationTests.Coordination;

public sealed class DevelopmentPlanPromotionTests
{
    private static readonly BoardDemandRecord ProductDemand = new(
        "tenant", "01KZ0000000000000000000001", "project", null,
        "Detalhar requisitos do controle de empréstimos",
        "O usuário pediu um sistema simples para controlar empréstimos e visualizar atrasos.",
        "open", "medium", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), false);

    private static readonly PlanMaterializationRequest DiscoveryRequest = new(
        ["Empréstimos atrasados ficam claramente visíveis."],
        "playbook-product-owner",
        new PlanMaterializationSurfaces(
            Frontend: false, Backend: false, ExternalCredential: false,
            TechnicalUncertainty: false, Decision: false));

    [Fact]
    public void ProductKickoffBecomesExecutableOnlyAfterDevelopmentRelease()
    {
        var beforeGate = PlanMaterializationService.PromoteRequestForDevelopment(
            DiscoveryRequest, ProductDemand,
            new ActiveWorkflowPhase("phase-4", "4-Planejamento", 4),
            chiefGenerated: true);
        Assert.Same(DiscoveryRequest, beforeGate);

        var released = PlanMaterializationService.PromoteRequestForDevelopment(
            DiscoveryRequest, ProductDemand,
            new ActiveWorkflowPhase("phase-5", "5-Desenvolvimento", 5),
            chiefGenerated: true);

        Assert.Null(released.Specialty);
        Assert.True(released.Surfaces?.Backend);
        Assert.True(released.Surfaces?.Frontend);
        Assert.Contains(released.AcceptanceCriteria,
            criterion => criterion.StartsWith(
                "DECISÃO DE PLANEJAMENTO [demanda:", StringComparison.Ordinal));

        var proposal = DemandDecompositionPlanner.Plan(new DemandDecompositionRequest(
            ProductDemand.Title,
            ProductDemand.Description,
            released.AcceptanceCriteria,
            ProductDemand.Priority,
            new DemandDecompositionHints(
                HasFrontendSurface: released.Surfaces?.Frontend,
                RequiresExternalCredential: released.Surfaces?.ExternalCredential,
                HasTechnicalUncertainty: released.Surfaces?.TechnicalUncertainty,
                RequiresDecision: released.Surfaces?.Decision,
                HasImplementationSurface: released.Surfaces?.Backend),
            released.Specialty));

        Assert.Contains(proposal.Cards,
            card => card.RequiredRole == DemandDecompositionPlanner.RoleBackend);
        Assert.Contains(proposal.Cards,
            card => card.RequiredRole == DemandDecompositionPlanner.RoleFrontend);
        Assert.DoesNotContain(proposal.Cards,
            card => string.Equals(card.Specialty, "playbook-product-owner", StringComparison.Ordinal));
    }

    /// <summary>
    /// O caso REAL desta operação: o compromisso já nasceu com superfície de código declarada pelo
    /// turno da chefe, então não há o que ampliar — e o retorno antecipado deixava a persona de
    /// DESCOBERTA colada nas fatias de implementação. Medido em 03/08: cinco cards de código
    /// nasceram pedindo `playbook-product-owner` para "implementar a fatia de servidor".
    /// </summary>
    [Fact]
    public void DeclaredSurfacesStillLoseTheDiscoveryPersonaAtDevelopment()
    {
        var alreadyCoded = DiscoveryRequest with
        {
            Surfaces = new PlanMaterializationSurfaces(
                Frontend: true, Backend: true, ExternalCredential: false,
                TechnicalUncertainty: false, Decision: false),
        };

        var released = PlanMaterializationService.PromoteRequestForDevelopment(
            alreadyCoded, ProductDemand,
            new ActiveWorkflowPhase("phase-5", "5-Desenvolvimento", 5),
            chiefGenerated: true);

        Assert.Null(released.Specialty);
        // Superfície declarada é decisão de quem declarou: a promoção não a reescreve.
        Assert.True(released.Surfaces?.Backend);
        Assert.True(released.Surfaces?.Frontend);
        Assert.Equal(alreadyCoded.AcceptanceCriteria, released.AcceptanceCriteria);

        // Antes da fase 5 nada muda: a persona de descoberta ainda é a certa.
        Assert.Same(
            alreadyCoded,
            PlanMaterializationService.PromoteRequestForDevelopment(
                alreadyCoded, ProductDemand,
                new ActiveWorkflowPhase("phase-4", "4-Planejamento", 4),
                chiefGenerated: true));
    }

    [Fact]
    public void ExplicitNoBuildInstructionIsNeverPromoted()
    {
        var demand = ProductDemand with
        {
            Description = "Quero somente requisitos para um sistema; não implementar nem construir.",
        };

        var result = PlanMaterializationService.PromoteRequestForDevelopment(
            DiscoveryRequest, demand,
            new ActiveWorkflowPhase("phase-5", "5-Desenvolvimento", 5),
            chiefGenerated: true);

        Assert.Same(DiscoveryRequest, result);
    }
}
