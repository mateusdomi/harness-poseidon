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

        foreach (var property in contractProperties)
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

    private ConversationChiefAgentExecutor Build(
        AgentAccountRegistry registry, FakeExternalExecutor fake) =>
        new(
            registry,
            new AgentAccountScheduler(),
            new AccountProfileProvisioner(_profilesRoot),
            _ => fake,
            new StubClock(Now),
            new ConversationChiefExecutorOptions(_repositoryRoot),
            NullLogger<ConversationChiefAgentExecutor>.Instance);

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
        AgentAccountState state, DateTimeOffset? cooldownUntil = null) =>
        new(
            "chief-claude-primary", "anthropic", ExecutorCatalog.ClaudeCode,
            "keychain://poseidon/chief-claude-primary", "confighome://chief-claude-primary",
            [AgentRoles.ChiefOrchestrator], [],
            state, AgentAccountHealth.Unknown, 1, 0, null, null, cooldownUntil, null, null, 100);

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
