using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class DemandDecompositionPlannerTests
{
    private static DemandDecompositionRequest Request(
        string title = "CAT-04 Publicar recursos do backend",
        string description = "Implementar o serviço de servidor e persistir os dados.",
        IReadOnlyList<string>? criteria = null,
        string risk = "medium",
        DemandDecompositionHints? hints = null) =>
        new(title, description, criteria ?? ["O endpoint responde 200."], risk, hints);

    [Fact]
    public void BackendOnlyDemandProducesBackendPlusIntegration()
    {
        var plan = DemandDecompositionPlanner.Plan(Request());

        Assert.Equal(2, plan.Cards.Count);
        var backend = plan.Cards[0];
        var integration = plan.Cards[1];
        Assert.Equal(DemandDecompositionPlanner.CardTypeAgentTask, backend.CardType);
        Assert.Equal(DemandDecompositionPlanner.RoleBackend, backend.RequiredRole);
        Assert.Equal(DemandDecompositionPlanner.CardTypeAgentTask, integration.CardType);
        Assert.Equal(DemandDecompositionPlanner.RoleCritic, integration.RequiredRole);
    }

    [Fact]
    public void FrontendSurfaceAddsFrontendTask()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Implementar a tela em React e o componente de UI.",
            criteria: ["A tela renderiza os dados."]));

        Assert.Contains(plan.Cards, c =>
            c.RequiredRole == DemandDecompositionPlanner.RoleFrontend &&
            c.CardType == DemandDecompositionPlanner.CardTypeAgentTask);
        // Backend continua presente; a crítica é o último card.
        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
        Assert.Equal(DemandDecompositionPlanner.RoleCritic, plan.Cards[^1].RequiredRole);
    }

    [Fact]
    public void FrontendHintOverridesAbsentKeywords()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Sem palavras de superfície.",
            hints: new DemandDecompositionHints(HasFrontendSurface: true)));

        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleFrontend);
    }

    [Fact]
    public void ExternalCredentialDemandEmitsHumanGate()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Integrar com o gateway de pagamento usando a credencial de homologação externa."));

        var gate = Assert.Single(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeHumanGate);
        Assert.Equal(DemandDecompositionPlanner.RoleNone, gate.RequiredRole);
    }

    [Fact]
    public void UncertainDemandEmitsSpike()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "É preciso investigar a viabilidade técnica; a abordagem ainda é desconhecida."));

        Assert.Single(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeSpike);
    }

    [Fact]
    public void DecisionKeywordEmitsDecisionCard()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Precisamos decidir e escolher entre as duas estratégias de rollout."));

        var decision = Assert.Single(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeDecision);
        Assert.Equal(DemandDecompositionPlanner.RoleNone, decision.RequiredRole);
    }

    [Fact]
    public void FeatureIdIsParsedAndPropagatedIntoChildTitles()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(title: "CAT-04 alguma coisa"));

        Assert.Equal("CAT-04", plan.FeatureId);
        Assert.All(plan.Cards, c => Assert.StartsWith("CAT-04/T", c.ProposedTitle, StringComparison.Ordinal));
        Assert.Equal("CAT-04/T01", DemandDecompositionPlanner.CodeOf(plan.Cards[0].ProposedTitle));
    }

    [Fact]
    public void MissingFeatureIdFallsBack()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(title: "Uma demanda sem código"));

        Assert.Equal(DemandDecompositionPlanner.FallbackFeatureId, plan.FeatureId);
        Assert.StartsWith("FEAT/T01", plan.Cards[0].ProposedTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalIntegrationCardDependsOnTheImplementationCards()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Implementar backend e a tela React de UI."));

        var integration = plan.Cards[^1];
        Assert.Equal(DemandDecompositionPlanner.RoleCritic, integration.RequiredRole);
        var implementationCodes = plan.Cards
            .Where(c => c.RequiredRole is DemandDecompositionPlanner.RoleBackend or DemandDecompositionPlanner.RoleFrontend)
            .Select(c => DemandDecompositionPlanner.CodeOf(c.ProposedTitle))
            .ToArray();
        Assert.NotEmpty(implementationCodes);
        Assert.All(implementationCodes, code => Assert.Contains(code, integration.Dependencies));
    }

    [Fact]
    public void ImplementationCardsDependOnSpikeAndHumanGate()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Investigar a viabilidade e usar a credencial externa de homologação; implementar o backend."));

        var spike = Assert.Single(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeSpike);
        var gate = Assert.Single(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeHumanGate);
        var backend = Assert.Single(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
        var spikeCode = DemandDecompositionPlanner.CodeOf(spike.ProposedTitle);
        var gateCode = DemandDecompositionPlanner.CodeOf(gate.ProposedTitle);
        Assert.Contains(spikeCode, backend.Dependencies);
        Assert.Contains(gateCode, backend.Dependencies);
        // O spike e o gate não têm dependências (são pré-requisitos).
        Assert.Empty(spike.Dependencies);
        Assert.Empty(gate.Dependencies);
    }

    [Fact]
    public void NeverAutoDispatchableCardTypesAreNotAgentTasks()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Investigar viabilidade; usar credencial externa; decidir estratégia; implementar backend e UI React."));

        foreach (var card in plan.Cards)
        {
            var isProtected = card.CardType is DemandDecompositionPlanner.CardTypeHumanGate
                or DemandDecompositionPlanner.CardTypeSpike or DemandDecompositionPlanner.CardTypeDecision;
            if (isProtected)
            {
                // Os avaliadores de prontidão só auto-despacham 'agent_task'; estes NUNCA são.
                Assert.NotEqual(DemandDecompositionPlanner.CardTypeAgentTask, card.CardType);
            }
        }
    }

    [Fact]
    public void PlanIsDeterministic()
    {
        var request = Request(
            description: "Investigar viabilidade; credencial externa de homologação; tela React de UI.");
        var first = DemandDecompositionPlanner.Plan(request);
        var second = DemandDecompositionPlanner.Plan(request);

        Assert.Equal(
            first.Cards.Select(c => (c.ProposedTitle, c.CardType, c.RequiredRole)),
            second.Cards.Select(c => (c.ProposedTitle, c.CardType, c.RequiredRole)));
        Assert.Equal(
            first.Cards.SelectMany(c => c.Dependencies),
            second.Cards.SelectMany(c => c.Dependencies));
    }

    [Fact]
    public void EmptyAcceptanceCriteriaStillProducesVerifiableCriteria()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(criteria: []));

        Assert.All(plan.Cards, c => Assert.NotEmpty(c.AcceptanceCriteria));
    }
}
