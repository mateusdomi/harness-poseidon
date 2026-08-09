using System.Reflection;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Agents.Infrastructure.Conversation;
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.UnitTests.Agents;

/// <summary>
/// GP-06: o Chefe responde DE VERDADE no chat, via CLI, pelo caminho leve. As provas aqui
/// não chamam a CLI real — o executor externo é substituído por um duble que captura o
/// pedido e devolve resultados canned. Cobrem: falha honesta sem conta, mapeamento
/// request→ExternalAgentRunRequest, parse da saída e reparo de saída fora do schema.
/// </summary>
public sealed class ConversationChiefAgentExecutorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly string _profilesRoot = Path.Combine(
        Path.GetTempPath(), $"harness-chief-chat-{Guid.NewGuid():N}");

    private readonly string _repositoryRoot = Path.Combine(
        Path.GetTempPath(), $"harness-chief-repo-{Guid.NewGuid():N}");

    public ConversationChiefAgentExecutorTests() => Directory.CreateDirectory(_repositoryRoot);

    private const string ValidChiefJson =
        """{"intent":"planejar_demanda","intentConfidence":0.9,"response":"Plano definido. Vou organizar a próxima entrega com a equipe.","demands":[]}""";

    [Fact]
    public async Task WithoutAChiefAccountItFailsHonestlyLikeUnavailable()
    {
        var executor = Build(new AgentAccountRegistry(), new FakeExternalExecutor(ValidChiefJson));

        await Assert.ThrowsAsync<AgentExecutorUnavailableException>(
            () => executor.ExecuteAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task ADisabledChiefAccountIsTreatedAsAbsent()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.Disabled));
        var executor = Build(registry, new FakeExternalExecutor(ValidChiefJson));

        await Assert.ThrowsAsync<AgentExecutorUnavailableException>(
            () => executor.ExecuteAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task AQuotaLimitedChiefAccountIsRejectedByTheScheduler()
    {
        // F-01: o executor deve consultar o AgentAccountScheduler, que rejeita uma conta
        // chief marcada como QuotaLimited com cooldown no futuro — mesmo sem snapshot de
        // cota observado. O resultado é a mesma falha honesta de uma conta ausente.
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.QuotaLimited, Now.AddHours(1)));
        var executor = Build(registry, new FakeExternalExecutor(ValidChiefJson));

        await Assert.ThrowsAsync<AgentExecutorUnavailableException>(
            () => executor.ExecuteAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task ItMapsTheRequestToAReadOnlyExternalRunOnTheRepositoryRoot()
    {
        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(ChiefRegistry(), fake);

        await executor.ExecuteAsync(
            Request(instruction: "Como está o projeto?", sessionId: "session-anterior"),
            CancellationToken.None);

        var captured = Assert.Single(fake.Requests);
        Assert.Equal("chief-claude-primary", captured.Alias);
        // Chat NUNCA edita: somente leitura fecha a escrita no processo.
        Assert.Equal(ExternalAgentAccess.ReadOnly, captured.Access);
        // Working directory é a raiz do repositório, não uma worktree de tentativa.
        Assert.Equal(Path.GetFullPath(_repositoryRoot), captured.WorkingDirectory);
        // Continuidade da conversa: a sessão anterior é retomada.
        Assert.Equal("session-anterior", captured.ResumeSessionId);
        // O perfil da conta é o config home isolado do alias.
        Assert.Equal("chief-claude-primary", captured.Profile.Alias);
        // O prompt carrega persona, contexto (digest) e a mensagem — nesta ordem de intenção.
        Assert.Contains("Bruna Magalhães", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("Diretora de Engenharia", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("Como está o projeto?", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"cards\":7", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("Formato de saída OBRIGATÓRIO", captured.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePromptLoadsGovernanceCoreFromTheHostDistribution()
    {
        // F-04: o executor carregava governance/core.md de <ControlledRoot>, que nunca existia,
        // e degradação para o fallback de 6 linhas era silenciosa. A governança é distribuída
        // junto com o binário do Host, então o prompt deve conter o texto real do núcleo.
        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(ChiefRegistry(), fake);

        await executor.ExecuteAsync(Request(), CancellationToken.None);

        var prompt = Assert.Single(fake.Requests).Prompt;
        Assert.Contains("Núcleo de governança do Poseidon", prompt, StringComparison.Ordinal);
        Assert.Contains("Precedência", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePromptLoadsBrunaPersonaFromRepositoryDocs()
    {
        // F-04-B: docs/agents/bruna.md é declarado como sourceOfTruth no manifesto, mas nunca era
        // aberto pelo código. O executor busca o arquivo no diretório da distribuição e na raiz do
        // repositório; verificamos que o conteúdo real (e não a persona embutida) entra no prompt.
        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(ChiefRegistry(), fake);

        await executor.ExecuteAsync(Request(), CancellationToken.None);

        var prompt = Assert.Single(fake.Requests).Prompt;
        // A persona embutida não tem esta seção; a persona viva em docs/agents/bruna.md tem.
        Assert.Contains("## Missão", prompt, StringComparison.Ordinal);
        Assert.Contains("## O princípio determinístico", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePromptTiesEscalatedCardsToTheCardActionThatClosesTheLoop()
    {
        // OPS-024: a chefe recebia o card escalado no contexto, respondia "decisão registrada e
        // aplicada" e não emitia cardActions — a decisão do dono morria como texto e o card
        // seguia escalado. O prompt precisa apontar ONDE o cardId vive (project.escalatedCards)
        // e mostrar um exemplo concreto da ação que fecha o laço. Se alguém "simplificar" o
        // prompt e remover isso, este teste reprova antes de o dono descobrir no chat.
        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(ChiefRegistry(), fake);

        await executor.ExecuteAsync(Request(), CancellationToken.None);

        var prompt = Assert.Single(fake.Requests).Prompt;
        Assert.Contains("project.escalatedCards", prompt, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"replan\"", prompt, StringComparison.Ordinal);
        Assert.Contains("cardActions", prompt, StringComparison.Ordinal);
        Assert.Contains("voltou a andar", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryContractFieldHasACounterpartInTheSerializationBridge()
    {
        // A ponte já perdeu um campo DUAS vezes (teamActions, depois cardActions — OPS-024): dois
        // records mantidos em sincronia à mão, e o sintoma é sempre o pior — a Bruna afirmando ao
        // dono ter feito algo que não fez. Este teste é o alarme do TERCEIRO campo: quem adicionar
        // uma propriedade ao contrato sem contraparte na ponte reprova aqui, em vez de na
        // conversa com o dono.
        var contractProperties = typeof(ChiefTurnOutput)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var bridge = typeof(ConversationChiefAgentExecutor)
            .GetNestedType("ChiefStructuredOutput", BindingFlags.NonPublic);
        Assert.NotNull(bridge);
        var bridgeProperties = bridge.GetProperties().Select(property => property.Name).ToArray();

        // Exceção DELIBERADA, não esquecimento: `contextRequests` (Onda 0.7) é consumido DENTRO
        // do executor — o pedido de seção vira uma rodada extra de contexto e é resolvido antes
        // da resposta final. Ele nunca é uma ação para o worker; atravessar a ponte seria vazar
        // uma etapa interna do turno como se fosse efeito sobre o mundo.
        string[] consumedInsideExecutor = [nameof(ChiefTurnOutput.ContextRequests)];

        foreach (var property in contractProperties.Except(consumedInsideExecutor))
        {
            Assert.True(
                bridgeProperties.Contains(property, StringComparer.Ordinal),
                $"A propriedade '{property}' do contrato não tem contraparte na ponte de serialização.");
        }
    }

    [Fact]
    public async Task ItParsesTheStructuredOutputAndPropagatesTheSessionForContinuity()
    {
        var fake = new FakeExternalExecutor(ValidChiefJson) { SessionId = "session-nova" };
        var executor = Build(ChiefRegistry(), fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal("conversation-chief", result.Executor);
        Assert.Equal("session-nova", result.SessionId);
        var output = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        Assert.Equal("Plano definido. Vou organizar a próxima entrega com a equipe.", output.Response);
        Assert.Empty(output.Demands);
        Assert.Equal(["Plano definido. Vou organizar a próxima entrega com a equipe."], result.Chunks);
    }

    [Fact]
    public async Task CommunicationLayerChangesOnlyTheConversationPrompt()
    {
        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(ChiefRegistry(), fake);

        await executor.ExecuteAsync(
            Request(communicationInstructions:
                "Chame Mateus pelo nome; use tom profissional, leve e afetuoso."),
            CancellationToken.None);

        var prompt = Assert.Single(fake.Requests).Prompt;
        Assert.Contains("Camada de comunicação com o usuário", prompt, StringComparison.Ordinal);
        Assert.Contains("Chame Mateus pelo nome", prompt, StringComparison.Ordinal);
        Assert.Contains("Ela não muda seu papel", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "a governança, o escopo técnico, os gates nem as regras de execução",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItToleratesACodeFenceAroundTheJsonObject()
    {
        var fenced =
            "Aqui está minha resposta:\n```json\n" + ValidChiefJson + "\n```\nAbraços.";
        var executor = Build(ChiefRegistry(), new FakeExternalExecutor(fenced));

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        var output = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        Assert.Equal("Plano definido. Vou organizar a próxima entrega com a equipe.", output.Response);
    }

    [Fact]
    public async Task ItRepairsAnOutOfSchemaResponseByResumingTheSameSession()
    {
        // Primeira resposta inválida (mas com sessão), segunda válida: reparo em UMA tentativa.
        var fake = new FakeExternalExecutor("desculpe, não consegui formatar")
        {
            SessionId = "session-reparo",
            NextMessages = new Queue<string>([ValidChiefJson]),
        };
        var executor = Build(ChiefRegistry(), fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, fake.Requests.Count);
        // A segunda chamada retoma a sessão emitida pela primeira.
        Assert.Equal("session-reparo", fake.Requests[1].ResumeSessionId);
        var output = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        Assert.Equal("Plano definido. Vou organizar a próxima entrega com a equipe.", output.Response);
    }

    [Fact]
    public async Task BusinessCommunicationLeakReceivesOneRepairBeforePublication()
    {
        const string leaked =
            """{"intent":"planejar_demanda","intentConfidence":0.9,"response":"O provider OpenAI falhou; veja o log e o turno 01ARZ3NDEKTSV4RRFFQ69G5FAV.","demands":[]}""";
        var fake = new FakeExternalExecutor(leaked)
        {
            SessionId = "session-policy-repair",
            NextMessages = new Queue<string>(
            [
                """{"intent":"planejar_demanda","intentConfidence":0.9,"response":"Houve uma falha temporária, mas nada foi perdido. Vou retomar com segurança.","demands":[]}""",
            ]),
        };
        var executor = Build(ChiefRegistry(), fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains(
            "política de comunicação obrigatória",
            fake.Requests[1].Prompt,
            StringComparison.OrdinalIgnoreCase);
        var output = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        Assert.Equal(
            "Houve uma falha temporária, mas nada foi perdido. Vou retomar com segurança.",
            output.Response);
    }

    [Fact]
    public async Task PrimaryRequirementsTechnicalRegisterAllowsProductVocabularyInBlindUnderstand()
    {
        const string response =
            """{"intent":"planejar_demanda","intentConfidence":0.93,"response":"Li os materiais do produto. A stack registrada é backend .NET, banco Oracle e frontend React. Falta apenas o repositório de destino.","demands":[]}""";
        var fake = new FakeExternalExecutor(response);
        var navigator = new FakeNavigator(
            [new ChiefPrimaryRequirementSource(
                "requisitos.md",
                "requirements_source",
                2,
                2,
                128,
                "O produto deve usar backend .NET, banco Oracle, API real e frontend React.")]);
        var executor = Build(ChiefRegistry(), fake, navigator);

        var result = await executor.ExecuteAsync(
            Request(instruction:
                "Bruna, analise os materiais deste projeto e prepare tudo o que for necessário para iniciarmos o desenvolvimento. Não inicie o desenvolvimento sem minha autorização."),
            CancellationToken.None);

        Assert.Single(fake.Requests);
        Assert.Contains("banco Oracle", ChiefTurnOutputContract.Parse(result.StructuredOutput).Response);
    }

    [Fact]
    public async Task TechnicalDetailsRequireBothExplicitRequestAndServerAuthorization()
    {
        const string technical =
            """{"intent":"planejar_demanda","intentConfidence":0.9,"response":"O provider OpenAI selecionou o modelo de análise.","demands":[]}""";

        var unauthorized = Build(ChiefRegistry(), new FakeExternalExecutor(technical)
        {
            SessionId = "session-unauthorized",
            NextMessages = new Queue<string>([technical]),
        });
        await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            unauthorized.ExecuteAsync(
                Request(
                    instruction: "Mostre os detalhes técnicos.",
                    communicationContext: new ChiefCommunicationContext(
                        TechnicalDetailsRequested: true)),
                CancellationToken.None));

        var authorized = Build(ChiefRegistry(), new FakeExternalExecutor(technical));
        var result = await authorized.ExecuteAsync(
            Request(
                instruction: "Mostre os detalhes técnicos.",
                communicationContext: new ChiefCommunicationContext(
                    TechnicalDetailsRequested: true,
                    TechnicalDetailsAuthorized: true)),
            CancellationToken.None);

        Assert.Equal(
            "O provider OpenAI selecionou o modelo de análise.",
            ChiefTurnOutputContract.Parse(result.StructuredOutput).Response);
    }

    [Fact]
    public async Task AnUnrecoverableOutOfSchemaResponseFailsHonestly()
    {
        var fake = new FakeExternalExecutor("nunca vira JSON")
        {
            SessionId = "session-x",
            NextMessages = new Queue<string>(["ainda não é JSON"]),
        };
        var executor = Build(ChiefRegistry(), fake);

        await Assert.ThrowsAsync<AgentOutputValidationException>(
            () => executor.ExecuteAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task AFailedExternalRunSurfacesTheTypedFailureCode()
    {
        var fake = new FakeExternalExecutor(ValidChiefJson)
        {
            Status = ExternalAgentRunStatus.Failed,
            FailureCode = "executor.quota_exhausted",
        };
        var executor = Build(ChiefRegistry(), fake);

        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.ExecuteAsync(Request(), CancellationToken.None));
        Assert.Equal("executor.quota_exhausted", exception.Code);
    }

    [Fact]
    public async Task TheChiefSeesTheSpecialistCatalogSheIsToldToDelegateTo()
    {
        // A persona dela manda "delegue ao especialista cuja persona melhor encaixa". Sem o
        // catálogo no prompt, essa instrução era irrealizável: a escolha caía numa heurística de
        // palavra-chave que alcança 5 das 25 personas semeadas.
        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(ChiefRegistry(), fake);

        await executor.ExecuteAsync(
            Request(specialists:
            [
                new AgentSpecialistOption("architecture-security", "Security Architect", "Ameaças"),
                new AgentSpecialistOption("software-engineer", "Software Engineer", null),
            ]),
            CancellationToken.None);

        var captured = Assert.Single(fake.Requests);
        Assert.Contains("architecture-security", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("Security Architect", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("software-engineer", captured.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyCatalogIsDeclaredInsteadOfInvented()
    {
        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(ChiefRegistry(), fake);

        await executor.ExecuteAsync(Request(), CancellationToken.None);

        var captured = Assert.Single(fake.Requests);
        Assert.Contains(
            "Nenhum especialista disponível no catálogo",
            captured.Prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeclaredSpecialtyAndSurfacesSurviveIntoTheStructuredOutput()
    {
        const string json =
            """
            {"intent":"planejar_demanda","intentConfidence":0.9,"response":"Só tela.","demands":[{"title":"UI-1 cor do botão","description":"Trocar a cor.",
            "riskTier":"low","acceptanceCriteria":["O botão fica verde."],
            "specialty":"software-engineer","surfaces":{"frontend":true,"backend":false}}]}
            """;
        var fake = new FakeExternalExecutor(json);
        var executor = Build(ChiefRegistry(), fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        var demand = Assert.Single(ChiefTurnOutputContract.Parse(result.StructuredOutput).Demands);
        Assert.Equal("software-engineer", demand.Specialty);
        Assert.True(demand.Surfaces!.Frontend);
        Assert.False(demand.Surfaces.Backend);
        // "Não declarei" continua distinto de "declarei que não".
        Assert.Null(demand.Surfaces.Decision);
    }

    [Fact]
    public async Task TeamActionsSurviveIntoTheStructuredOutput()
    {
        // Achado ao vivo: ela anunciou ao dono ter formado um especialista e o catálogo continuou
        // igual. O campo existia no contrato e era aceito na leitura, mas a reserialização — a
        // ÚNICA ponte entre o que o modelo produziu e o que o worker executa — não o reescrevia.
        // O que não passa por aqui não acontece, por mais convincente que seja a prosa.
        const string json =
            """
            {"intent":"planejar_demanda","intentConfidence":0.9,"response":"Formei o especialista.","demands":[],
             "teamActions":[{"action":"create_persona",
               "reason":"Nenhuma persona do catálogo cobre auditoria de acessibilidade WCAG.",
               "persona":{"key":"accessibility-auditor","name":"Auditor de Acessibilidade",
                 "purpose":"Auditar conformidade WCAG 2.2 AA e emitir laudo por critério.",
                 "specialty":"accessibility","responsibilities":["Auditar"],"constraints":[],
                 "requiredCapabilities":["repo.read"],"riskTiers":["medium"]}}]}
            """;
        var fake = new FakeExternalExecutor(json);
        var executor = Build(ChiefRegistry(), fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        var parsed = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        var action = Assert.Single(parsed.TeamActions!);
        Assert.Equal("create_persona", action.Action);
        Assert.Equal("accessibility-auditor", action.Persona!.Key);
        Assert.Equal(["repo.read"], action.Persona.RequiredCapabilities);
    }

    [Fact]
    public async Task CardActionsSurviveIntoTheStructuredOutput()
    {
        // OPS-024 (a ponte, de novo): a chefe recebia o card escalado no contexto, emitia a
        // cardActions correta — e ela NUNCA chegava ao worker, porque a reserialização da saída
        // estruturada não reescrevia o campo. O dono ouvia "decisão registrada e aplicada" e o
        // card seguia escalated. O que não passa por aqui não acontece.
        const string json =
            """
            {"intent":"decidir_escalacao","intentConfidence":0.93,
             "response":"Decisão registrada: vou redirecionar esse trabalho com o escopo menor.",
             "demands":[],
             "cardActions":[{"action":"replan","cardId":"01KZ26X4RN6V96W19ZXTKKGTJE",
               "instruction":"Reescrever o plano com escopo reduzido: histórico simples."}]}
            """;
        var fake = new FakeExternalExecutor(json);
        var executor = Build(ChiefRegistry(), fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        var parsed = ChiefTurnOutputContract.Parse(result.StructuredOutput);
        var action = Assert.Single(parsed.CardActions!);
        Assert.Equal("replan", action.Action);
        Assert.Equal("01KZ26X4RN6V96W19ZXTKKGTJE", action.CardId);
    }

    /// <summary>
    /// Onda 0.7 de ponta a ponta no executor: a primeira resposta pede seções via
    /// `contextRequests`; o executor busca o conteúdo INTEGRAL no navegador de anexos, reinvoca
    /// retomando a MESMA sessão com as seções como DADO, e a resposta final é a que vale.
    /// </summary>
    [Fact]
    public async Task AContextRequestFetchesTheFullSectionAndTheSecondAnswerWins()
    {
        var fake = new FakeExternalExecutor(
            """{"intent":"responder_pergunta","intentConfidence":0.9,"response":"Preciso ler a metodologia antes de afirmar.","demands":[],"contextRequests":[{"file":"espec.md","sections":["5","99"]}]}""")
        {
            NextMessages = new Queue<string>(
                ["""{"intent":"responder_pergunta","intentConfidence":0.95,"response":"Com a seção em mãos: a criticidade sai da tabela de consulta.","demands":[]}"""]),
        };
        var navigator = new FakeNavigator();
        var executor = Build(ChiefRegistry(), fake, navigator);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, fake.Requests.Count);
        // O prompt do turno declara o índice navegável e o caminho do conteúdo integral.
        Assert.Contains("índice navegável", fake.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("§5 — 5. Metodologia", fake.Requests[0].Prompt, StringComparison.Ordinal);
        // A rodada de seções retoma a MESMA sessão e carrega a seção INTEIRA como DADO.
        Assert.Equal("session-fake", fake.Requests[1].ResumeSessionId);
        Assert.Contains("conteúdo INTEGRAL", fake.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("tabela de consulta completa", fake.Requests[1].Prompt, StringComparison.Ordinal);
        // Seção inexistente volta como ausência DECLARADA, nunca inventada.
        Assert.Contains("seção 99 — NÃO ENCONTRADA", fake.Requests[1].Prompt, StringComparison.Ordinal);
        // A resposta final é a da segunda rodada.
        Assert.Contains("a criticidade sai da tabela de consulta", result.StructuredOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutANavigatorAContextRequestDegradesToTheFirstAnswer()
    {
        // Sem navegador registrado o pedido de seção não pode ser atendido: o turno mantém a
        // primeira resposta válida (comportamento anterior à Onda 0.7), sem rodada extra.
        var fake = new FakeExternalExecutor(
            """{"intent":"responder_pergunta","intentConfidence":0.9,"response":"Só tenho o resumo do anexo por enquanto.","demands":[],"contextRequests":[{"file":"espec.md","sections":["5"]}]}""");
        var executor = Build(ChiefRegistry(), fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Single(fake.Requests);
        Assert.Contains("tenho o resumo do anexo por enquanto", result.StructuredOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Onda 4.4 — isolamento da flag do grafo no prompt: SEM digest (flag off), o prompt não
    /// ganha nenhuma seção de grafo, nem cabeçalho vazio; COM digest, a seção aparece com o
    /// conteúdo como DADO.
    /// </summary>
    [Fact]
    public async Task OPromptSoGanhaSecaoDeGrafoQuandoODigestExiste()
    {
        var off = new FakeExternalExecutor(ValidChiefJson);
        await Build(ChiefRegistry(), off).ExecuteAsync(Request(), CancellationToken.None);
        Assert.DoesNotContain(
            "Impacto do projeto (grafo)",
            Assert.Single(off.Requests).Prompt,
            StringComparison.Ordinal);

        var on = new FakeExternalExecutor(ValidChiefJson);
        await Build(ChiefRegistry(), on).ExecuteAsync(
            Request() with { ImpactDigest = "Grafo do projeto v7 — 3 nós, 2 arestas." },
            CancellationToken.None);
        var prompt = Assert.Single(on.Requests).Prompt;
        Assert.Contains("## Impacto do projeto (grafo) — DADO", prompt, StringComparison.Ordinal);
        Assert.Contains("Grafo do projeto v7", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Onda 0.3 — "duas candidatas a Chief, uma vence": com duas contas ELEGÍVEIS o scheduler
    /// escolhe exatamente uma, por prioridade (empate: alias canônico), independentemente da
    /// ordem de registro. Nunca duas Chefs ativas: um turno = uma conta.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTwoCandidatesExactlyOneChiefWinsDeterministically(bool reverseRegistration)
    {
        var registry = new AgentAccountRegistry();
        var primary = ChiefAccount(AgentAccountState.Available, alias: "chief-claude-primary", priority: 2);
        var secondary = ChiefAccount(AgentAccountState.Available, alias: "chief-claude-secondary", priority: 1);
        foreach (var account in reverseRegistration ? [secondary, primary] : new[] { primary, secondary })
        {
            registry.Register(account);
        }

        var fake = new FakeExternalExecutor(ValidChiefJson);
        var executor = Build(registry, fake);

        await executor.ExecuteAsync(Request(), CancellationToken.None);

        var captured = Assert.Single(fake.Requests);
        Assert.Equal("chief-claude-primary", captured.Alias);
    }

    /// <summary>
    /// Onda 0.3 — o failover da Chefe: a titular morre por causa da CONTA (cota esgotada) e a
    /// secundária assume o MESMO turno, automaticamente. O dono recebe a resposta; a troca fica
    /// no log, não na cara dele.
    /// </summary>
    [Fact]
    public async Task WhenThePrimaryDiesForAccountReasonsTheSecondaryTakesTheSameTurn()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.Available, alias: "chief-claude-primary", priority: 2));
        registry.Register(ChiefAccount(AgentAccountState.Available, alias: "chief-claude-secondary", priority: 1));
        var fake = new FailingByAliasExecutor(
            ValidChiefJson, "chief-claude-primary",
            "executor.quota_exhausted", ExternalFailureKind.QuotaExhausted);
        var executor = Build(registry, fake);

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("chief-claude-primary", fake.Requests[0].Alias);
        Assert.Equal("chief-claude-secondary", fake.Requests[1].Alias);
        Assert.Contains("Plano definido", result.StructuredOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Failover é para falha DA CONTA. Timeout/transiente na titular não justifica dobrar o
    /// custo do mesmo problema noutra conta: propaga o código tipado e o worker decide o retry.
    /// </summary>
    [Fact]
    public async Task ATransientFailureDoesNotTriggerFailover()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.Available, alias: "chief-claude-primary", priority: 2));
        registry.Register(ChiefAccount(AgentAccountState.Available, alias: "chief-claude-secondary", priority: 1));
        var fake = new FailingByAliasExecutor(
            ValidChiefJson, "chief-claude-primary",
            "executor.timeout", ExternalFailureKind.Timeout);
        var executor = Build(registry, fake);

        var exception = await Assert.ThrowsAsync<ExternalAgentException>(
            () => executor.ExecuteAsync(Request(), CancellationToken.None));

        Assert.Equal("executor.timeout", exception.Code);
        var captured = Assert.Single(fake.Requests);
        Assert.Equal("chief-claude-primary", captured.Alias);
    }

    /// <summary>Duble que falha SOMENTE para um alias, com tipo de falha declarado.</summary>
    private sealed class FailingByAliasExecutor(
        string message, string failingAlias, string failureCode, ExternalFailureKind failureKind)
        : IExternalAgentExecutor
    {
        public List<ExternalAgentRunRequest> Requests { get; } = [];

        public string ExecutorId => ExecutorCatalog.ClaudeCode;

        public ExecutorProfile Profile => ExecutorCatalog.Find(ExecutorCatalog.ClaudeCode)!;

        public Task<ExecutorProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutorProbeResult(
                ExecutorId, true, "2.1.216", AgentAccountState.Available, "ok"));

        public Task<IExternalAgentSession> StartAsync(
            ExternalAgentRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var fails = string.Equals(request.Alias, failingAlias, StringComparison.Ordinal);
            var result = new ExternalAgentRunResult(
                ExecutorId, request.Alias, "session-fake",
                fails ? ExternalAgentRunStatus.Failed : ExternalAgentRunStatus.Completed,
                fails ? string.Empty : message,
                [], null, fails ? 1 : 0, fails ? failureCode : null, 5)
            {
                FailureKind = fails ? failureKind : ExternalFailureKind.Unknown,
            };
            return Task.FromResult<IExternalAgentSession>(new FakeSession(result));
        }
    }

    private sealed class FakeNavigator(
        IReadOnlyList<ChiefPrimaryRequirementSource>? primaryRequirements = null)
        : IChiefAttachmentNavigator
    {
        public Task<IReadOnlyList<ChiefAttachmentOutline>> ListOutlinesAsync(
            string tenantId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChiefAttachmentOutline>>(
                [new ChiefAttachmentOutline(
                    "espec.md",
                    [new ChiefAttachmentSectionRef("5", "5. Metodologia de avaliação")])]);

        public Task<string?> ReadSectionAsync(
            string tenantId, string projectId, string fileName, string sectionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(sectionId == "5"
                ? "## 5. Metodologia de avaliação\n\ncritérios de impacto e a tabela de consulta completa"
                : null);

        public Task<IReadOnlyList<ChiefPrimaryRequirementSource>> ListPrimaryRequirementSourcesAsync(
            string tenantId,
            string projectId,
            int maximumCharacters,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(primaryRequirements ?? []);
    }

    private ConversationChiefAgentExecutor Build(
        AgentAccountRegistry registry,
        IExternalAgentExecutor fake,
        IChiefAttachmentNavigator? navigator = null) =>
        new(
            registry,
            new AgentAccountScheduler(),
            new AccountProfileProvisioner(_profilesRoot),
            _ => fake,
            new StubClock(Now),
            new ConversationChiefExecutorOptions(_repositoryRoot),
            NullLogger<ConversationChiefAgentExecutor>.Instance,
            navigator);

    private static AgentExecutionRequest Request(
        string instruction = "Continue com segurança",
        string? sessionId = null,
        string? communicationInstructions = null,
        IReadOnlyList<AgentSpecialistOption>? specialists = null,
        ChiefCommunicationContext? communicationContext = null) =>
        new(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
            "chief-orchestrator",
            instruction,
            """{"cards":7,"status":"green"}""",
            "/unused/by/this/executor",
            sessionId,
            CommunicationInstructions: communicationInstructions,
            Specialists: specialists,
            CommunicationContext: communicationContext);

    private static AgentAccountRegistry ChiefRegistry()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.Available));
        return registry;
    }

    private static AgentAccountContract ChiefAccount(
        AgentAccountState state,
        DateTimeOffset? cooldownUntil = null,
        string alias = "chief-claude-primary",
        int priority = 1) =>
        new(
            alias, "anthropic", ExecutorCatalog.ClaudeCode,
            $"keychain://poseidon/{alias}", $"confighome://{alias}",
            [AgentRoles.ChiefOrchestrator], [],
            state, AgentAccountHealth.Unknown, priority, 0, null, null, cooldownUntil, null, null, 100);

    public void Dispose()
    {
        foreach (var path in new[] { _profilesRoot, _repositoryRoot })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    /// <summary>
    /// Duble do executor externo: captura cada <see cref="ExternalAgentRunRequest"/> e devolve
    /// um resultado canned. Nunca inicia processo nem toca a CLI real.
    /// </summary>
    private sealed class FakeExternalExecutor(string firstMessage) : IExternalAgentExecutor
    {
        public List<ExternalAgentRunRequest> Requests { get; } = [];

        public string? SessionId { get; init; } = "session-fake";

        public ExternalAgentRunStatus Status { get; init; } = ExternalAgentRunStatus.Completed;

        public string? FailureCode { get; init; }

        public Queue<string> NextMessages { get; init; } = new();

        public string ExecutorId => ExecutorCatalog.ClaudeCode;

        public ExecutorProfile Profile => ExecutorCatalog.Find(ExecutorCatalog.ClaudeCode)!;

        public Task<ExecutorProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutorProbeResult(
                ExecutorId, true, "2.1.216", AgentAccountState.Available, "ok"));

        public Task<IExternalAgentSession> StartAsync(
            ExternalAgentRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var message = Requests.Count == 1 || NextMessages.Count == 0
                ? firstMessage
                : NextMessages.Dequeue();
            var result = new ExternalAgentRunResult(
                ExecutorId, request.Alias, SessionId, Status, message, [], null, 0,
                Status == ExternalAgentRunStatus.Completed ? null : FailureCode, 5);
            return Task.FromResult<IExternalAgentSession>(new FakeSession(result));
        }
    }

    private sealed class FakeSession(ExternalAgentRunResult result) : IExternalAgentSession
    {
        public string RunId => "run-fake";

        public string? SessionId => result.SessionId;

        public int ProcessId => 0;

        public bool IsRunning => false;

        public IAsyncEnumerable<ExternalAgentEvent> StreamAsync(
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ExternalAgentEvent>();

        public Task<ExternalAgentRunResult> CollectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
