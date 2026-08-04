using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class DemandDecompositionPlannerTests
{
    private static DemandDecompositionRequest Request(
        string title = "CAT-04 Publicar recursos do backend",
        string description = "Implementar o serviço de servidor e persistir os dados.",
        IReadOnlyList<string>? criteria = null,
        string risk = "medium",
        DemandDecompositionHints? hints = null,
        string? specialty = null) =>
        new(title, description, criteria ?? ["O endpoint responde 200."], risk, hints, specialty);

    [Fact]
    public void BackendOnlyDemandProducesJustTheServerSlice()
    {
        var plan = DemandDecompositionPlanner.Plan(Request());

        // Não existe mais gate humano de integração por demanda: a revisão independente acontece
        // em cada card, o merge do card revisado é feito pela própria chefe e a verificação ponta a
        // ponta virou obrigação da FASE. O card só acrescentaria um item indespachável ao board,
        // esperando um clique que não decide mais nada.
        var backend = Assert.Single(plan.Cards);
        Assert.Equal(DemandDecompositionPlanner.CardTypeAgentTask, backend.CardType);
        Assert.Equal(DemandDecompositionPlanner.RoleBackend, backend.RequiredRole);
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
        // O rito que sobrou é o que exige o mundo externo (decisão entre alternativas), não o
        // pedágio de integração.
        Assert.Contains(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeDecision);
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
        // Backend continua presente, e o plano termina no trabalho — não num gate de cerimônia.
        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
        Assert.DoesNotContain(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeHumanGate);
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
    public void NoCeremonialIntegrationGateIsEmittedForTheDemand()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            description: "Implementar backend e a tela React de UI."));

        // As duas fatias de implementação existem e o plano acaba nelas.
        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleFrontend);
        Assert.DoesNotContain(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeHumanGate);
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

    [Fact]
    public void APurelyVisualDemandDoesNotEmitAServerSlice()
    {
        // Observado três vezes na homologação, com consequência real: "mudar a cor do botão" e
        // "propor um redesign da tela" nasciam com card de BACKEND junto. Alguém executaria esse
        // card e gastaria cota escrevendo código de servidor que ninguém pediu.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "UI-11: proposta de redesign visual do painel",
            description: "Produzir duas ou três direções visuais da tela, com dados fictícios, para o dono escolher.",
            criteria: ["O dono escolhe uma das direções apresentadas."]));

        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleFrontend);
        Assert.DoesNotContain(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
    }

    [Fact]
    public void ADemandThatTouchesScreenAndServerKeepsBothSlices()
    {
        // A guarda é do PURAMENTE visual: mencionando servidor, as duas fatias continuam.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "AUT-12: filtro de vendas por vendedor",
            description: "Aplicar o filtro na tela e garantir que o endpoint tambem restrinja os dados por vendedor."));

        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleFrontend);
        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
    }

    [Fact]
    public void TheDeclaredSpecialtyReachesOnlyTheCardsAnAgentExecutes()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "SEC-01: revisar a sessão",
            description: "Investigar a abordagem e implementar o endpoint de sessão na tela e no servidor.",
            specialty: "architecture-security"));

        foreach (var card in plan.Cards)
        {
            if (card.CardType == DemandDecompositionPlanner.CardTypeAgentTask)
            {
                Assert.Equal("architecture-security", card.Specialty);
            }
            else
            {
                // Gate humano, spike e decisão não têm persona executora: carimbá-los sugeriria
                // um dono que o card não tem.
                Assert.Null(card.Specialty);
            }
        }

        Assert.Contains(plan.Cards, c => c.CardType == DemandDecompositionPlanner.CardTypeAgentTask);
    }

    [Fact]
    public void WithoutADeclaredSpecialtyTheCardsCarryNone()
    {
        var plan = DemandDecompositionPlanner.Plan(Request());
        Assert.All(plan.Cards, card => Assert.Null(card.Specialty));
    }

    [Fact]
    public void ABlankSpecialtyIsTreatedAsAbsent()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(specialty: "   "));
        Assert.All(plan.Cards, card => Assert.Null(card.Specialty));
    }

    [Fact]
    public void TheChiefDeclaringNoImplementationSurfaceRemovesTheServerSlice()
    {
        // A pendência (b) da homologação: o julgamento da chefe chegava à demanda e morria ali,
        // porque o turno passava `hints: null`. Aqui o texto GRITA implementação de servidor
        // ("implementar", "endpoint", "persistir") e mesmo assim a declaração dela prevalece.
        var plan = DemandDecompositionPlanner.Plan(Request(
            hints: new DemandDecompositionHints(HasImplementationSurface: false)));

        Assert.DoesNotContain(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleBackend);
    }

    [Fact]
    public void ADemandWithoutAnySurfaceStillGetsSomeoneToProduceTheDeliverable()
    {
        // Achado ao vivo: "fazer o threat model", "escrever o plano de testes" e "escrever o
        // README" nasceram com gates e ZERO cards produtores. Spike investiga, decisão escolhe,
        // gate verifica — nenhum deles entrega. O gate de integração ficava esperando para sempre
        // por cards de implementação que não existiam.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "SEC-02: threat model do login",
            description: "Mapear as ameaças do fluxo de login e apontar onde a cadeia de ataque quebra.",
            criteria: ["Cada ameaça tem um controle correspondente."],
            hints: new DemandDecompositionHints(HasFrontendSurface: false, HasImplementationSurface: false)));

        var producer = Assert.Single(
            plan.Cards, card => card.CardType == DemandDecompositionPlanner.CardTypeAgentTask);
        Assert.Equal(DemandDecompositionPlanner.RoleNone, producer.RequiredRole);
        // Os critérios da demanda viajam com quem produz, senão o entregável não é verificável.
        Assert.Equal(["Cada ameaça tem um controle correspondente."], producer.AcceptanceCriteria);
        // E o produtor é o ÚNICO card do plano: nenhum gate de cerimônia sobra esperando clique.
        Assert.Single(plan.Cards);
    }

    [Fact]
    public void ALowRiskDemandWithoutSurfacesAlsoGetsAProducer()
    {
        // Risco baixo dispensa cerimônia — não dispensa alguém encarregado do resultado.
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "DOC-01: README do piloto",
            description: "Escrever o README e o runbook de operação. Sem código.",
            risk: "low",
            hints: new DemandDecompositionHints(HasImplementationSurface: false)));

        Assert.Contains(plan.Cards, card => card.CardType == DemandDecompositionPlanner.CardTypeAgentTask);
    }

    [Fact]
    public void APlanThatAlreadyHasAProducerDoesNotGainASecondOne()
    {
        var plan = DemandDecompositionPlanner.Plan(Request());
        Assert.Single(plan.Cards, card => card.CardType == DemandDecompositionPlanner.CardTypeAgentTask);
        Assert.DoesNotContain(
            plan.Cards, card => card.ProposedTitle.Contains("Entregável", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFallbackProducerCarriesTheDeclaredSpecialty()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "SEC-02: threat model",
            description: "Mapear as ameaças do fluxo de login.",
            hints: new DemandDecompositionHints(HasImplementationSurface: false),
            specialty: "architecture-security"));

        var producer = Assert.Single(
            plan.Cards, card => card.CardType == DemandDecompositionPlanner.CardTypeAgentTask);
        Assert.Equal("architecture-security", producer.Specialty);
    }

    [Fact]
    public void TheChiefDeclaringAFrontendSurfaceAddsTheSliceTheTextNeverMentions()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            hints: new DemandDecompositionHints(HasFrontendSurface: true)));

        Assert.Contains(plan.Cards, c => c.RequiredRole == DemandDecompositionPlanner.RoleFrontend);
    }

    /// <summary>
    /// Regressão da prova limpa de 04/08/2026: os cards de implementação nasceram sem nenhum
    /// pré-requisito num projeto onde a tecnologia concreta não estava decidida. O ator recusou —
    /// com razão, citando o plano aceito — e quatro tentativas queimaram ~38 mil tokens sem um
    /// único commit. A decisão precisa virar card, e o card precisa ser EXECUTÁVEL.
    /// </summary>
    [Fact]
    public void AnImplementationPlanWithoutADecidedStackLeadsWithTheArchitectureDecision()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            hints: new DemandDecompositionHints(
                HasFrontendSurface: true, RequiresArchitectureDecision: true)));

        var adr = Assert.Single(
            plan.Cards, card => card.CardType == DemandDecompositionPlanner.CardTypeAdr);

        // Primeiro card do plano: é dele que todo o resto depende.
        Assert.Same(plan.Cards[0], adr);
        Assert.Equal(DemandDecompositionPlanner.SpecialtyArchitect, adr.Specialty);
        Assert.Empty(adr.Dependencies);

        var code = adr.ProposedTitle.Split(' ')[0];
        var implementation = plan.Cards
            .Where(card => card.CardType == DemandDecompositionPlanner.CardTypeAgentTask)
            .ToArray();
        Assert.Equal(2, implementation.Length);
        Assert.All(implementation, card => Assert.Contains(code, card.Dependencies));
    }

    /// <summary>
    /// A metade que impede o erro simétrico. `decision` é humano e NÃO despachável
    /// (<c>CardReadinessEvaluator.DispatchableCardTypes</c>): se a decisão de stack nascesse com
    /// aquele tipo, a implementação esperaria para sempre por um card que agente nenhum executa —
    /// trocaríamos "implementa sem decidir" por "nunca implementa".
    /// </summary>
    [Fact]
    public void TheArchitectureDecisionIsDispatchableUnlikeTheHumanDecision()
    {
        Assert.Contains(
            DemandDecompositionPlanner.CardTypeAdr,
            CardReadinessEvaluator.DispatchableCardTypes);
        Assert.DoesNotContain(
            DemandDecompositionPlanner.CardTypeDecision,
            CardReadinessEvaluator.DispatchableCardTypes);
    }

    /// <summary>
    /// Uma demanda cujo entregável é documento não decide stack nenhuma: não há código para o ADR
    /// destravar, e o card só acrescentaria espera.
    /// </summary>
    [Fact]
    public void ADocumentDemandDoesNotGetAStackDecision()
    {
        var plan = DemandDecompositionPlanner.Plan(Request(
            title: "DOC-01: registrar a stack no manual",
            description: "Documentar a stack concreta já adotada pelo time no guia de onboarding.",
            hints: new DemandDecompositionHints(HasImplementationSurface: false)));

        Assert.DoesNotContain(
            plan.Cards, card => card.CardType == DemandDecompositionPlanner.CardTypeAdr);
    }
}
