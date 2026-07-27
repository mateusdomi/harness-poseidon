using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harness.Host.Agents;
using Harness.Host.Governance;
using Harness.Host.Leadership;
using Harness.Host.Observability;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Conversations.Application;
using Harness.Modules.Conversations.Domain;
using Harness.Modules.Coordination.Application;
using Harness.Host.WorkBoard;
using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Evaluation;
using Harness.Modules.Governance.Memory;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Cockpit;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workers;

public sealed partial class ChiefTurnBackgroundService(
    IChiefTurnStore turns,
    ICockpitDigestStore digests,
    IGovernanceRuntimeStore governance,
    ContextBundleBuilder bundleBuilder,
    IRagContextProvider ragContext,
    IFreshContextEvaluator evaluator,
    IAgentExecutor executor,
    IClock clock,
    ChiefTurnWorkerOptions options,
    ChiefContextComposer contextComposer,
    ChiefContextStrategyOptions contextStrategyOptions,
    LeadershipProfileStore leadershipProfile,
    ILocalProfileStore localProfiles,
    IWorkBoardStore board,
    DemandPlanMaterializer demandPlans,
    IAgentCatalogStore agentCatalog,
    IConversationStore conversations,
    ChiefTeamManager teamManager,
    ILogger<ChiefTurnBackgroundService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _ownerId = $"chief-worker:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        try
        {
            do
            {
                while (await ProcessNextAsync(stoppingToken))
                {
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal do Host; não deve contaminar logs nem disparar StopHost.
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A durable mailbox worker must isolate one failed agent turn and persist a retry decision.")]
    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        ChiefTurnLease? lease;
        try
        {
            lease = await turns.AcquireNextAsync(
                _ownerId, clock.UtcNow, options.LeaseDuration, cancellationToken);
        }
        catch (ChiefTurnConflictException)
        {
            // DISPUTA DE LEASE NÃO É FALHA DO SERVIÇO. A seleção do próximo turno e a aquisição do
            // lease são dois passos: entre eles, outro worker (ou o mesmo processo antes de um
            // restart) pode ter assumido o projeto. Como esta aquisição acontecia FORA do try, a
            // exceção subia até o BackgroundService — e, com `BackgroundServiceExceptionBehavior`
            // em StopHost, derrubava o HOST INTEIRO por uma condição de corrida rotineira.
            // Observado ao vivo: o Poseidon morria e as conversas de todos os projetos com ele.
            // Agora o ciclo apenas cede a vez e tenta de novo no próximo tick.
            LogTurnLeaseContended(logger, nameof(ChiefTurnConflictException));
            return false;
        }

        if (lease is null) return false;
        using var turnActivity = PoseidonTelemetry.StartChiefTurn(
            lease.Turn.TenantId,
            lease.Turn.ProjectId,
            lease.Turn.ConversationId,
            lease.Turn.TurnId,
            lease.ChiefAgentId);
        var turnStartedAt = Stopwatch.GetTimestamp();
        GovernanceTurnReceiptRecord? receipt = null;

        // C3+: reporta as fases granulares reais pelas quais o turno passa como
        // eventos chief.turnStateChanged (com heartbeat lastActivityAt), para o
        // balão da conversa distinguir "trabalhando" de "travado". Observabilidade
        // honesta: só reporta fases que de fato acontecem, nunca resposta fabricada.
        async Task ReportAsync(ChiefTurnActivity activity, string? detail = null)
        {
            await turns.RecordActivityAsync(
                new ChiefTurnActivityCommand(
                    lease.Turn.TenantId, lease.Turn.ProjectId, lease.Turn.ConversationId,
                    lease.Turn.TurnId, ChiefTurnActivityState.Wire(activity), clock.UtcNow,
                    AgentName: null, ActivityStartedAt: clock.UtcNow, Detail: detail),
                cancellationToken);
        }

        try
        {
            await ReportAsync(ChiefTurnActivity.ReadingContext);
            var digest = await digests.ReadAsync(
                lease.Turn.TenantId, lease.Turn.ProjectId, 20, cancellationToken);
            var digestJson = JsonSerializer.Serialize(digest, JsonOptions);
            IReadOnlyList<RagContextSlice> memory = options.ContextBundlesEnabled
                ? await ragContext.SearchAsync(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Instruction,
                    cancellationToken: cancellationToken)
                : [];
            ContextBundle bundle;
            using (var contextActivity = PoseidonTelemetry.StartChiefContext())
            {
                bundle = bundleBuilder.BuildOrFallback(new ContextBundleRequest(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    lease.ChiefAgentId,
                    "poseidon",
                    lease.Turn.Selection?.ModelName,
                    "chief-turn",
                    "execution",
                    "orchestration",
                    "medium",
                    [],
                    digestJson,
                    ["Return a schema-valid Chief response.", "Persist durable completion evidence."],
                    ["Domain writes only through typed Host stores."],
                    [],
                    ["Stop on canonical conflict, secret risk, invalid output or failed gate."],
                    options.ContextBundlesEnabled ? options.ContextTokenBudget : 256,
                    memory.Select(slice => new ContextMemorySlice(
                        slice.DocumentId,
                        slice.Content,
                        slice.CitationReference,
                        slice.TokenCount)).ToArray()));
                contextActivity?.SetTag("context.document_count", bundle.Documents.Count);
                contextActivity?.SetTag("context.estimated_tokens", bundle.EstimatedTokens);
                contextActivity?.SetTag("context.truncated_count", bundle.Truncated.Count);
                contextActivity?.SetTag("context.cache_hits", bundle.CacheHits);
            }
            await governance.CreateContextSnapshotAsync(
                ContextSnapshotFactory.Create(
                    lease.Turn.TenantId,
                    lease.Turn.TurnId,
                    lease.Turn.ProjectId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    bundle,
                    clock.UtcNow),
                cancellationToken);
            receipt = await governance.CreateReceiptAsync(
                new GovernanceTurnReceiptCreateCommand(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    lease.ChiefAgentId,
                    bundle.ManifestVersion,
                    bundle.Documents.Select(document => new GovernanceReceiptDocumentRecord(
                        document.DocumentId,
                        document.Checksum,
                        document.SelectionReason,
                        document.LoadPolicy.ToString(),
                        document.EstimatedTokens)).ToArray(),
                    bundle.EstimatedTokens,
                    bundle.Truncated,
                    bundle.Conflicts,
                    bundle.CacheHits,
                    lease.Turn.Selection?.Source ?? "poseidon",
                    lease.Turn.Selection?.ModelName,
                    clock.UtcNow,
                    bundle.BundleChecksum),
                cancellationToken);
            await AppendBundleMetricsAsync(governance, lease, bundle, clock.UtcNow, cancellationToken);
            if (bundle.Conflicts.Count > 0)
            {
                throw new ContextBundleConflictException(bundle.Conflicts);
            }

            receipt = await governance.CompleteReceiptAsync(
                new GovernanceTurnReceiptCompleteCommand(
                    lease.Turn.TenantId,
                    lease.Turn.TurnId,
                    receipt.Version,
                    null,
                    GovernanceReceiptState.Delivered,
                    null,
                    clock.UtcNow),
                cancellationToken);
            // PLAT-02: quando a estratégia de contexto está ligada, monta a janela de trabalho
            // limitada (compactação + limpeza de tool-result) e externaliza os fatos críticos como
            // notas duráveis ANTES de invocar o modelo. Desligada (default), o contexto governado é
            // byte a byte idêntico ao comportamento anterior — nenhuma regressão.
            string governedDigestJson;
            if (contextStrategyOptions.Enabled)
            {
                var composition = await contextComposer.ComposeAsync(
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.ConversationId,
                    lease.Turn.TurnId,
                    cancellationToken);
                governedDigestJson = JsonSerializer.Serialize(
                    new ChiefGovernanceContextWithMemory(
                        digestJson,
                        bundle.RenderedContext,
                        bundle.BundleChecksum,
                        composition.RenderedContext,
                        composition.PersistedNoteCount),
                    JsonOptions);
            }
            else
            {
                governedDigestJson = JsonSerializer.Serialize(
                    new ChiefGovernanceContext(digestJson, bundle.RenderedContext, bundle.BundleChecksum),
                    JsonOptions);
            }
            await ReportAsync(ChiefTurnActivity.Thinking);
            var communicationInstructions =
                (await leadershipProfile.ReadAsync(cancellationToken)).CommunicationInstructions;
            var specialists = await ReadSpecialistCatalogAsync(lease.Turn.TenantId, cancellationToken);
            AgentExecutionResult execution;
            using (var invocationActivity = PoseidonTelemetry.StartChiefInvocation(
                       lease.Turn.Selection?.Source,
                       lease.Turn.Selection?.ModelName))
            {
                execution = await executor.ExecuteAsync(
                    new AgentExecutionRequest(
                        lease.Turn.TenantId,
                        lease.Turn.ProjectId,
                        lease.Turn.ConversationId,
                        lease.ChiefAgentId,
                        lease.Instruction,
                        governedDigestJson,
                        AppContext.BaseDirectory,
                        lease.SessionId,
                        lease.Turn.Selection?.ModelName,
                        lease.Turn.Selection?.ProviderEffortValue,
                        communicationInstructions,
                        specialists),
                    cancellationToken);
                invocationActivity?.SetTag("gen_ai.response.model", lease.Turn.Selection?.ModelName);
                invocationActivity?.SetTag("gen_ai.client.operation.duration_ms", execution.DurationMs);
                invocationActivity?.SetTag("agent.executor", execution.Executor);
            }
            var output = ChiefTurnOutputContract.Parse(execution.StructuredOutput);
            await ReportAsync(ChiefTurnActivity.Planning);
            var evaluation = evaluator.Evaluate(
                new FreshContextEvaluationRequest(
                    $"evaluation:{lease.Turn.TurnId}",
                    lease.Turn.TenantId,
                    lease.Turn.ProjectId,
                    lease.Turn.TurnId,
                    lease.Turn.TurnId,
                    lease.ChiefAgentId,
                    $"{lease.ChiefAgentId}:critic",
                    "medium",
                    ["Chief output must conform to the structured contract."],
                    execution.StructuredOutput,
                    [$"executor={execution.Executor};durationMs={execution.DurationMs}"],
                    [new EvaluationTestResult("structured-output", true, "ChiefTurnOutputContract.Parse")]),
                clock.UtcNow);
            await governance.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.EvaluatorVerdict, null, null,
                    evaluation.Verdict.ToString().ToLowerInvariant(), clock.UtcNow),
                cancellationToken);
            if (evaluation.Verdict == EvaluationVerdict.Fail)
            {
                throw new AgentOutputValidationException("Independent evaluator returned Default-FAIL.");
            }
            var chunks = execution.Chunks.Count == 0 ? new[] { output.Response } : execution.Chunks;
            var occurredAt = clock.UtcNow;
            var message = ConversationApplicationService.CreateChiefMessage(
                UlidValue.New(occurredAt).ToString(),
                lease.Turn.ConversationId,
                lease.ChiefAgentId,
                output.Response,
                occurredAt);
            var demandSeeds = output.Demands
                .Select((demand, index) => new ChiefDemandSeed(
                    UlidValue.New(occurredAt.AddMilliseconds(10 + index * 2)).ToString(),
                    UlidValue.New(occurredAt.AddMilliseconds(11 + index * 2)).ToString(),
                    demand.Title,
                    demand.Description,
                    demand.RiskTier,
                    demand.AcceptanceCriteria,
                    demand.Specialty,
                    demand.Surfaces is null
                        ? null
                        : new ChiefDemandSurfaceDeclaration(
                            demand.Surfaces.Frontend,
                            demand.Surfaces.Backend,
                            demand.Surfaces.ExternalCredential,
                            demand.Surfaces.TechnicalUncertainty,
                            demand.Surfaces.Decision)))
                .ToArray();
            if (demandSeeds.Length > 0)
            {
                // O Chefe está delegando: demandas serão materializadas para agentes.
                await ReportAsync(
                    ChiefTurnActivity.Delegating,
                    demandSeeds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            await turns.CompleteAsync(
                new ChiefTurnCompleteCommand(
                    lease,
                    new MessageRecord(
                        lease.Turn.TenantId, lease.Turn.ProjectId, message.Id,
                        message.ConversationId, message.AuthorRole, message.AuthorProfileId,
                        message.AuthorAgentId, message.Content, message.TokenCount, message.CreatedAt),
                    chunks,
                    execution.SessionId,
                    digestJson,
                    occurredAt,
                    demandSeeds),
                cancellationToken);
            receipt = await governance.CompleteReceiptAsync(
                new GovernanceTurnReceiptCompleteCommand(
                    lease.Turn.TenantId,
                    lease.Turn.TurnId,
                    receipt.Version,
                    null,
                    GovernanceReceiptState.Completed,
                    "pass",
                    clock.UtcNow),
                cancellationToken);
            await governance.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.GateResult, null, null, "pass", clock.UtcNow),
                cancellationToken);

            // A promessa de delegação da Bruna vira TRABALHO REAL: cada demanda materializada no
            // turno é decomposta em plano e materializada em cards do board (backlog), de onde a
            // triagem por ondas + o loop autônomo assumem. Falha aqui NUNCA falha o turno (a
            // resposta já foi entregue de forma durável); cada demanda é isolada e logada.
            if (demandSeeds.Length > 0)
            {
                await MaterializeDemandPlansAsync(lease, demandSeeds, cancellationToken);
            }

            // GESTÃO DE EQUIPE: quando nenhuma persona do catálogo cobre a demanda, a chefe cria o
            // especialista e delega — sem esperar o dono, que é stakeholder e não RH da fábrica.
            // Falha aqui NUNCA falha o turno (a resposta já foi entregue de forma durável).
            if (output.TeamActions is { Count: > 0 } teamActions)
            {
                await ApplyTeamActionsAsync(lease, teamActions, cancellationToken);
            }

            turnActivity?.SetTag("chief.result", "completed");
            PoseidonTelemetry.RecordChiefTurn(
                "completed",
                Stopwatch.GetElapsedTime(turnStartedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turnActivity?.SetTag("chief.result", "cancelled");
            PoseidonTelemetry.RecordChiefTurn(
                "cancelled",
                Stopwatch.GetElapsedTime(turnStartedAt).TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            turnActivity?.SetStatus(ActivityStatusCode.Error);
            turnActivity?.SetTag("error.type", exception.GetType().FullName);
            if (receipt is not null && receipt.State is not GovernanceReceiptState.Completed and not GovernanceReceiptState.Failed)
            {
                receipt = await governance.CompleteReceiptAsync(
                    new GovernanceTurnReceiptCompleteCommand(
                        lease.Turn.TenantId,
                        lease.Turn.TurnId,
                        receipt.Version,
                        null,
                        GovernanceReceiptState.Failed,
                        "fail",
                        clock.UtcNow),
                    cancellationToken);
                await governance.AppendMetricAsync(
                    Metric(lease, GovernanceMetricKind.GateResult, null, null, "fail", clock.UtcNow),
                    cancellationToken);
            }
            var retryable = exception is not AgentOutputValidationException;
            var outcome = await turns.FailAsync(
                new ChiefTurnFailCommand(
                    lease, exception.GetType().Name, clock.UtcNow, retryable),
                cancellationToken);
            LogTurnFailure(
                logger,
                lease.Turn.TurnId,
                exception.GetType().Name,
                retryable);

            // O turno morreu: o usuário perguntou e NINGUÉM ia responder. Até aqui o fato ficava
            // só no mailbox e no log — do lado de fora, a conversa simplesmente parava, sem
            // resposta e sem erro. Quem responde pelo projeto é o chefe, então é ele quem conta a
            // má notícia, com o código técnico e o id do turno para o caso ser reconstruído.
            if (outcome.Terminal)
            {
                await AnnounceTerminalFailureAsync(
                    lease, exception.GetType().Name, cancellationToken);
            }
            var result = retryable ? "retryable_failure" : "terminal_failure";
            turnActivity?.SetTag("chief.result", result);
            PoseidonTelemetry.RecordChiefTurn(
                result,
                Stopwatch.GetElapsedTime(turnStartedAt).TotalMilliseconds);
        }

        return true;
    }

    /// <summary>
    /// Elo demanda→cards do turno: para cada demanda semeada pelo turno da Bruna, gera o plano
    /// determinístico e o materializa em cards reais (idempotente por demanda/plano). O autor dos
    /// cards é o perfil local do tenant — a mesma autoria usada pelo loop autônomo. Uma demanda
    /// que falhar não impede as demais nem o turno (já completado); o erro fica no log.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "One failed demand must not poison the other demands nor the completed turn.")]
    private async Task MaterializeDemandPlansAsync(
        ChiefTurnLease lease,
        IReadOnlyList<ChiefDemandSeed> seeds,
        CancellationToken cancellationToken)
    {
        var profile = (await localProfiles.ListAsync(cancellationToken))
            .FirstOrDefault(candidate => string.Equals(
                candidate.TenantId, lease.Turn.TenantId, StringComparison.Ordinal));
        if (profile is null)
        {
            return;
        }

        foreach (var seed in seeds)
        {
            try
            {
                var demand = await board.GetDemandAsync(
                    lease.Turn.TenantId, seed.DemandId, cancellationToken);
                if (demand is null)
                {
                    continue;
                }

                // O julgamento do Chefe chega ao PLANO. Antes, as dicas eram sempre nulas: ele
                // declarava na conversa que a demanda era só visual (ou que exigia decisão antes de
                // construir) e o planner, cego a isso, refazia a leitura por palavra-chave do texto
                // — às vezes contra o que ele havia acabado de concluir.
                var saved = await demandPlans.EnsurePlanAsync(
                    lease.Turn.TenantId, demand, seed.AcceptanceCriteria, ToHints(seed.Surfaces),
                    seed.Specialty, clock.UtcNow, cancellationToken);
                var outcome = await demandPlans.MaterializeAsync(
                    lease.Turn.TenantId, profile.Id, saved.Plan, demand,
                    clock.UtcNow, cancellationToken);
                if (!outcome.AlreadyMaterialized)
                {
                    LogDemandMaterialized(
                        logger, seed.DemandId, lease.Turn.TurnId, outcome.Cards.Count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDemandMaterializationFailure(
                    logger, seed.DemandId, lease.Turn.TurnId, exception.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Aplica as intenções de gestão de equipe do turno. Uma ação recusada pela policy ou pelo
    /// catálogo é registrada e seguida — a chefe continua com quem já existe, e o turno, que já
    /// respondeu ao usuário, não é derrubado por isso.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed team action must not poison the completed turn.")]
    private async Task ApplyTeamActionsAsync(
        ChiefTurnLease lease,
        IReadOnlyList<ChiefTeamAction> actions,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = (await localProfiles.ListAsync(cancellationToken))
                .FirstOrDefault(candidate => string.Equals(
                    candidate.TenantId, lease.Turn.TenantId, StringComparison.Ordinal));
            if (profile is null)
            {
                return;
            }

            var results = await teamManager.ApplyAsync(
                lease.Turn.TenantId, lease.Turn.ProjectId, profile.Id, actions, cancellationToken);
            foreach (var result in results)
            {
                LogTeamAction(logger, lease.Turn.TurnId, result.Action, result.ReasonCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogTeamActionFailure(logger, lease.Turn.TurnId, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Publica, na própria conversa, que a mensagem do usuário NÃO foi respondida. É o único
    /// lugar em que a falha terminal vira informação para quem perguntou; sem isto a conversa
    /// morre em silêncio e o usuário fica esperando uma resposta que não vem.
    ///
    /// A mensagem é do chefe (a única voz com o usuário), diz o que aconteceu, o código técnico e
    /// o identificador do turno — sem prompt, sem resposta parcial do modelo e sem segredo. Uma
    /// falha AQUI não pode mascarar a falha original, então nada é propagado.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Announcing the failure must never mask the failure being announced.")]
    private async Task AnnounceTerminalFailureAsync(
        ChiefTurnLease lease,
        string errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var occurredAt = clock.UtcNow;
            var content =
                "Não consegui processar sua última mensagem. Tentei três vezes e parei — nenhuma " +
                "delas chegou a uma resposta, então prefiro dizer isso a deixar você esperando.\n\n" +
                $"Código técnico: `{errorCode}` · turno `{lease.Turn.TurnId}`.\n\n" +
                "Nada foi decidido nem delegado a partir dessa mensagem. Pode reenviá-la que eu " +
                "retomo do zero; se falhar de novo, o código acima é o fio para investigar.";
            var message = ConversationApplicationService.CreateChiefMessage(
                UlidValue.New(occurredAt).ToString(),
                lease.Turn.ConversationId,
                lease.ChiefAgentId,
                content,
                occurredAt);
            await conversations.CreateMessageAsync(
                new MessageCreateCommand(
                    lease.Turn.TenantId,
                    new MessageRecord(
                        lease.Turn.TenantId, lease.Turn.ProjectId, message.Id,
                        message.ConversationId, message.AuthorRole, message.AuthorProfileId,
                        message.AuthorAgentId, message.Content, message.TokenCount, message.CreatedAt),
                    occurredAt),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogTerminalAnnouncementFailure(logger, lease.Turn.TurnId, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Traduz a superfície declarada pelo Chefe nas dicas do planner. Nulo em ambos os lados
    /// significa "não declarei": o planner segue inferindo do texto, como antes.
    /// </summary>
    private static DemandDecompositionHints? ToHints(ChiefDemandSurfaceDeclaration? surfaces) =>
        surfaces is null
            ? null
            : new DemandDecompositionHints(
                surfaces.Frontend,
                surfaces.ExternalCredential,
                surfaces.TechnicalUncertainty,
                surfaces.Decision,
                surfaces.Backend);

    /// <summary>
    /// Catálogo de especialistas oferecido ao Chefe para que ele delegue a quem é qualificado.
    /// Falha de leitura NÃO derruba o turno: o catálogo volta vazio e o prompt declara a ausência,
    /// em vez de apresentar uma lista inventada.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A missing specialist catalog degrades the prompt; it must never fail the turn.")]
    private async Task<IReadOnlyList<AgentSpecialistOption>> ReadSpecialistCatalogAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var definitions = await agentCatalog.ListDefinitionsForTenantAsync(
                tenantId, null, 100, false, cancellationToken);
            return [.. definitions
                .Where(definition =>
                    definition.Enabled &&
                    definition.ArchivedAt is null &&
                    string.Equals(definition.Role, "specialist", StringComparison.OrdinalIgnoreCase))
                .OrderBy(definition => definition.Key, StringComparer.Ordinal)
                .Select(definition => new AgentSpecialistOption(
                    definition.Key, definition.Name, definition.Specialty))];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSpecialistCatalogUnavailable(logger, exception.GetType().Name);
            return [];
        }
    }

    private static async Task AppendBundleMetricsAsync(
        IGovernanceRuntimeStore store,
        ChiefTurnLease lease,
        ContextBundle bundle,
        DateTimeOffset now,
        CancellationToken token)
    {
        var index = 0;
        foreach (var document in bundle.Documents)
        {
            await store.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.Selected, document.DocumentId, null, null,
                    now.AddTicks(index++)), token);
        }

        foreach (var documentId in bundle.Truncated)
        {
            await store.AppendMetricAsync(
                Metric(lease, GovernanceMetricKind.ItemTruncated, documentId, null, null,
                    now.AddTicks(index++)), token);
        }

        await store.AppendMetricAsync(
            Metric(lease, GovernanceMetricKind.Delivered, null, null, bundle.BundleChecksum,
                now.AddTicks(index)), token);
    }

    private static GovernanceMetricAppendCommand Metric(
        ChiefTurnLease lease,
        GovernanceMetricKind kind,
        string? documentId,
        string? ruleId,
        string? detail,
        DateTimeOffset at) => new(
        lease.Turn.TenantId,
        lease.Turn.ProjectId,
        lease.Turn.TurnId,
        UlidValue.New(at).ToString(),
        kind,
        documentId,
        ruleId,
        detail,
        null,
        at);

    private sealed record ChiefGovernanceContext(
        string StatusDigestJson,
        string ContextBundle,
        string BundleChecksum);

    private sealed record ChiefGovernanceContextWithMemory(
        string StatusDigestJson,
        string ContextBundle,
        string BundleChecksum,
        string ChiefContext,
        int PersistedNoteCount);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Chief turn {TurnId} failed with {ErrorType}; retryable={Retryable}.")]
    private static partial void LogTurnFailure(
        ILogger logger,
        string turnId,
        string errorType,
        bool retryable);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Chief: demanda {DemandId} do turno {TurnId} materializada em {CardCount} card(s).")]
    private static partial void LogDemandMaterialized(
        ILogger logger,
        string demandId,
        string turnId,
        int cardCount);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Chief: materialização da demanda {DemandId} do turno {TurnId} falhou: {ErrorType}.")]
    private static partial void LogDemandMaterializationFailure(
        ILogger logger,
        string demandId,
        string turnId,
        string errorType);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Warning,
        Message = "Chief: catálogo de especialistas indisponível ({ErrorType}); o turno segue sem opções de delegação.")]
    private static partial void LogSpecialistCatalogUnavailable(ILogger logger, string errorType);

    [LoggerMessage(
        EventId = 2108,
        Level = LogLevel.Information,
        Message = "Chief: lease do projeto disputado ({ErrorType}); o ciclo cede a vez e tenta no próximo tick.")]
    private static partial void LogTurnLeaseContended(ILogger logger, string errorType);

    [LoggerMessage(
        EventId = 2106,
        Level = LogLevel.Information,
        Message = "Chief: ação de equipe '{Action}' do turno {TurnId} resolvida como {ReasonCode}.")]
    private static partial void LogTeamAction(
        ILogger logger, string turnId, string action, string reasonCode);

    [LoggerMessage(
        EventId = 2107,
        Level = LogLevel.Warning,
        Message = "Chief: gestão de equipe do turno {TurnId} falhou: {ErrorType}.")]
    private static partial void LogTeamActionFailure(ILogger logger, string turnId, string errorType);

    [LoggerMessage(
        EventId = 2105,
        Level = LogLevel.Error,
        Message = "Chief: falha terminal do turno {TurnId} NÃO pôde ser anunciada ao usuário ({ErrorType}).")]
    private static partial void LogTerminalAnnouncementFailure(
        ILogger logger, string turnId, string errorType);
}
