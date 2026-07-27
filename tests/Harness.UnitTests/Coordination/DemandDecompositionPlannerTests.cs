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
        // A integração é GATE HUMANO: o merge é humano por regra e a revisão independente já
        // acontece em cada card de implementação. Emiti-la como 'agent_task' com papel 'critic'
        // criava um card estruturalmente indespachável — o papel crítico não tem escopo de escrita,
        // então o loop colhia `agent_path_scope_empty` a cada ciclo, para sempre.
        Assert.Equal(DemandDecompositionPlanner.CardTypeHumanGate, integration.CardType);
        Assert.Equal(DemandDecompositionPlanner.RoleNone, integration.RequiredRole);
    }

    [Fact]
    public void ALowRiskDemandDoesNotPayForTheFullCeremony()
    {
        // Achado da homologação: a chefe classificou "mudar a cor do botão para verde" como risco
        // BAIXO — e o plano mesmo assim abria card de documentação e GATE HUMANO. Uma troca de cor
        // ficava parada esperando alguém clicar. A revisão independente do card e o gate humano de
        // MERGE continuam valendo, então dispensar o rito extra não perde segurança nenhuma.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "UI-09: alterar a cor do botão de exportar para verde",
            description: "Trocar a cor do botão de exportar na tela de vendas. Só a cor.",
            risk: "low"));

        Assert.DoesNotContain(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeHumanGate);
        Assert.DoesNotContain(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeSpike);
        Assert.DoesNotContain(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeDecision);
        Assert.All(plan.Cards, c => Assert.Equal(DemandDecompositionPlanner.CardTypeAgentTask, c.CardType));
    }

    [Fact]
    public void RiskAboveLowKeepsTheFullCeremony()
    {
        // A dispensa é do trivial, não da regra: risco médio ou maior mantém o rito completo.
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Investigar a viabilidade e implementar o serviço; decidir a abordagem.",
            risk: "high"));

        Assert.Contains(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeSpike);
        Assert.Contains(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeHumanGate);
    }

    [Fact]
    public void CardTitlesCarryTheSubjectOfTheDemandInsteadOfAGenericLabel()
    {
        // Achado da homologação: com várias demandas no mesmo projeto, o board virava uma lista de
        // cards indistinguíveis ("Backend: implementar a fatia de servidor" repetido N vezes). O
        // raciocínio da chefe chegava à demanda e morria ali, sem chegar ao card que o executor lê.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "AUT-07: Autenticação e autorização por vendedor",
            description: "Implementar login e filtro de vendas por vendedor na tela e no servidor."));

        Assert.All(plan.Cards, card => Assert.Contains("vendedor", card.ProposedTitle, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Cards, card => card.ProposedTitle.StartsWith("AUT-07/T", StringComparison.Ordinal));
    }

    [Fact]
    public void AGenericLabelStillCoversADemandWithoutAUsableSubject()
    {
        // Sem assunto aproveitável o rótulo genérico continua valendo — título vazio nunca.
        var plan = DemandDecompositionPlanner.Plan(Request(title: "X"));

        Assert.All(plan.Cards, card => Assert.False(string.IsNullOrWhiteSpace(card.ProposedTitle)));
        Assert.Contains(plan.Cards, card => card.ProposedTitle.Contains("fatia de servidor", StringComparison.Ordinal));
    }

    [Fact]
    public void ADemandWhoseDeliverableIsADecisionDoesNotEmitABackendSlice()
    {
        // Achado da homologação: a chefe abriu uma demanda de ADR ("definir a abordagem") e o
        // plano emitia, junto, um card de "implementar a fatia de servidor" — mandando um agente
        // escrever código de produção do que ainda nem foi decidido.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "ADR: arquitetura do painel",
            description: "Decidir entre arquivo e banco de dados para a persistência, com trade-off registrado.",
            criteria: ["A decisão está registrada com o porquê."]));

        Assert.Contains(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeDecision);
        Assert.DoesNotContain(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
    }

    [Fact]
    public void ADemandThatAsksToBuildStillEmitsTheBackendSlice()
    {
        // A guarda não pode virar desculpa para não construir: havendo pedido de construção, a
        // fatia de backend continua sendo a linha de base, mesmo com decisão no meio do caminho.
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Decidir o formato do payload e implementar o endpoint de vendas do dia."));

        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
    }

    [Fact]
    public void TheChiefCanDeclareTheDemandHasNoImplementationSurface()
    {
        // O hint é a via pela qual o raciocínio da chefe chega ao plano sem depender de palavra-chave.
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Implementar e construir o serviço completo.",
            hints: new DemandDecompositionHints(HasImplementationSurface: false)));

        Assert.DoesNotContain(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
    }

    [Fact]
    public void NoPlannedCardIsEverDispatchableWithoutAWriteScope()
    {
        // Trava de regressão: todo card auto-despachável ('agent_task') precisa de um papel que
        // POSSUA escopo de escrita, senão a tentativa nasce condenada a agent_path_scope_empty.
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Implementar a tela em React, o endpoint no backend e atualizar a documentação."));

        Assert.All(
            plan.Cards.Where(card => card.CardType == DemandDecompositionPlanner.CardTypeAgentTask),
            card => Assert.NotEqual(DemandDecompositionPlanner.RoleCritic, card.RequiredRole));
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
        // Backend continua presente; o gate humano de integração é o último card.
        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
        Assert.Equal(DemandDecompositionPlanner.CardTypeHumanGate, plan.Cards[^1].CardType);
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

        // O gate de credencial é o pré-requisito (sem dependências); o de integração fecha o plano.
        var gate = Assert.Single(plan.Cards, c =>
            c.CardType == DemandDecompositionPlanner.CardTypeHumanGate && c.Dependencies.Count == 0);
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
        Assert.Equal(DemandDecompositionPlanner.CardTypeHumanGate, integration.CardType);
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
        // Dois gates humanos coexistem: o de credencial (pré-requisito) e o de integração (final).
        // Aqui interessa o de credencial — o único sem dependências.
        var gate = Assert.Single(plan.Cards, c =>
            c.CardType == DemandDecompositionPlanner.CardTypeHumanGate && c.Dependencies.Count == 0);
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

    [Fact]
    public void TheInstructionDescribesTheDemandNotTheArchitectureOfAnotherProject()
    {
        // Achado da homologação: a instrução do card mandava aplicar "tenant scope, OCC e
        // persistência dual" em QUALQUER demanda — inclusive numa CLI Python de projeto-cliente,
        // onde nada disso existe. É o texto que o executor lê antes de escrever código.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "CUR-02: adicionar libra esterlina ao conversor",
            description: "Implementar o suporte a GBP no conversor de moedas."));

        var backend = Assert.Single(
            plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
        Assert.Contains("libra esterlina", backend.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OCC", backend.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant scope", backend.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("persistência dual", backend.Instruction, StringComparison.OrdinalIgnoreCase);
    }
}
