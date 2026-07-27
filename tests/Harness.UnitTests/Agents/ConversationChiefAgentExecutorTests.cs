using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Agents.Infrastructure.Conversation;
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.SharedKernel.Time;

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
        """{"response":"Plano definido. Vou delegar a fatia ao backend.","demands":[]}""";

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
        Assert.Contains("Chief Orchestrator", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("Como está o projeto?", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"cards\":7", captured.Prompt, StringComparison.Ordinal);
        Assert.Contains("Formato de saída OBRIGATÓRIO", captured.Prompt, StringComparison.Ordinal);
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
        Assert.Equal("Plano definido. Vou delegar a fatia ao backend.", output.Response);
        Assert.Empty(output.Demands);
        Assert.Equal(["Plano definido. Vou delegar a fatia ao backend."], result.Chunks);
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
        Assert.Equal("Plano definido. Vou delegar a fatia ao backend.", output.Response);
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
        Assert.Equal("Plano definido. Vou delegar a fatia ao backend.", output.Response);
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
            {"response":"Só tela.","demands":[{"title":"UI-1 cor do botão","description":"Trocar a cor.",
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
            {"response":"Formei o especialista.","demands":[],
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

    private ConversationChiefAgentExecutor Build(
        AgentAccountRegistry registry, FakeExternalExecutor fake) =>
        new(
            registry,
            new AccountProfileProvisioner(_profilesRoot),
            _ => fake,
            new StubClock(Now),
            new ConversationChiefExecutorOptions(_repositoryRoot));

    private static AgentExecutionRequest Request(
        string instruction = "Continue com segurança",
        string? sessionId = null,
        string? communicationInstructions = null,
        IReadOnlyList<AgentSpecialistOption>? specialists = null) =>
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
            Specialists: specialists);

    private static AgentAccountRegistry ChiefRegistry()
    {
        var registry = new AgentAccountRegistry();
        registry.Register(ChiefAccount(AgentAccountState.Available));
        return registry;
    }

    private static AgentAccountContract ChiefAccount(AgentAccountState state) =>
        new(
            "chief-claude-primary", "anthropic", ExecutorCatalog.ClaudeCode,
            "keychain://poseidon/chief-claude-primary", "confighome://chief-claude-primary",
            [AgentRoles.ChiefOrchestrator], [],
            state, AgentAccountHealth.Unknown, 1, 0, null, null, null, null, null, 100);

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
