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
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Licensing;
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
    IAgentCatalogStore agentCatalog,
    IChiefTurnIntentStore turnIntents,
    IConversationStore conversations,
    ILicenseStore licenses,
    ChiefTeamManager teamManager,
    IServiceScopeFactory scopes,
    ILogger<ChiefTurnBackgroundService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> ReasonCodeTranslations =
        ReasonCodeHumanizer.KnownCodes.ToDictionary(
            code => code,
            code => ReasonCodeHumanizer.Humanize(code).Compose(),
            StringComparer.Ordinal);
    private readonly string _ownerId = $"chief-worker:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly ChiefTurnWorkerOptions _validatedOptions = Validated(options);

    private static ChiefTurnWorkerOptions Validated(ChiefTurnWorkerOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return value;
    }

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
        var communicationContext = ChiefCommunicationPolicy.Business;

        // C3+: reporta as fases granulares reais pelas quais o turno passa como
        // eventos chief.turnStateChanged (com heartbeat lastActivityAt), para o
        // balão da conversa distinguir "trabalhando" de "travado". Observabilidade
        // honesta: só reporta fases que de fato acontecem, nunca resposta fabricada.
        async Task<DateTimeOffset> ReportAsync(
            ChiefTurnActivity activity,
            string? detail = null,
            DateTimeOffset? activityStartedAt = null,
            CancellationToken? reportToken = null)
        {
            var occurredAt = clock.UtcNow;
            await turns.RecordActivityAsync(
                new ChiefTurnActivityCommand(
                    lease.Turn.TenantId, lease.Turn.ProjectId, lease.Turn.ConversationId,
                    lease.Turn.TurnId, ChiefTurnActivityState.Wire(activity), occurredAt,
                    AgentName: null,
                    ActivityStartedAt: activityStartedAt ?? occurredAt,
                    Detail: detail),
                reportToken ?? cancellationToken);
            return occurredAt;
        }

        async Task MaintainActivityHeartbeatAsync(
            ChiefTurnActivity activity,
            DateTimeOffset activityStartedAt,
            CancellationTokenSource leaseLost,
            CancellationToken heartbeatToken)
        {
            using var timer = new PeriodicTimer(options.ActivityHeartbeatInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(heartbeatToken))
                {
                    try
                    {
                        // Fase 0A2 (BR-005): o batimento RENOVA o lease, não apenas pinta a tela.
                        // Sem isso, uma inferência mais longa que o lease deixava o turno vivo
                        // parecer abandonado: outro worker o readquiria, chamava o modelo de novo
                        // e o dono pagava duas vezes pela mesma pergunta.
                        var renewal = await turns.TryRenewAsync(
                            new ChiefTurnRenewCommand(lease, clock.UtcNow, options.LeaseDuration),
                            heartbeatToken);
                        if (!renewal.Renewed)
                        {
                            // Perdemos o fencing: outro dono assumiu o turno. Continuar seria
                            // gastar cota para produzir um resultado que o store vai recusar.
                            LogTurnLeaseLost(logger, lease.Turn.TurnId, lease.FencingToken);
                            PoseidonTelemetry.RecordChiefTurnLease("lost");
                            await leaseLost.CancelAsync();
                            return;
                        }

                        PoseidonTelemetry.RecordChiefTurnLease("renewed");
                        await ReportAsync(
                            activity,
                            activityStartedAt: activityStartedAt,
                            reportToken: heartbeatToken);
                    }
                    catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // Telemetria de liveness é importante, mas uma falha transitória ao
                        // gravá-la não invalida a resposta já calculada nem deve duplicar custo.
                        LogActivityHeartbeatFailure(
                            logger,
                            lease.Turn.TurnId,
                            exception.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
            {
                // A chamada terminou ou o host está encerrando; não há mais liveness a renovar.
            }
        }

        // Cancelado quando o batimento descobre que o fencing foi perdido. Um turno tomado por
        // outro dono precisa PARAR aqui: qualquer escrita nossa seria recusada pelo store, e a
        // inferência em curso viraria custo puro.
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await ReportAsync(ChiefTurnActivity.ReadingContext);
            var digest = await digests.ReadAsync(
                lease.Turn.TenantId, lease.Turn.ProjectId, 20, cancellationToken);
            var digestJson = JsonSerializer.Serialize(digest, JsonOptions);
            var projectContext = await contextComposer.ComposeProjectAsync(
                lease.Turn.TenantId,
                lease.Turn.ProjectId,
                cancellationToken);
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
                        projectContext,
                        ReasonCodeTranslations,
                        composition.RenderedContext,
                        composition.PersistedNoteCount),
                    JsonOptions);
            }
            else
            {
                governedDigestJson = JsonSerializer.Serialize(
                    new ChiefGovernanceContext(
                        digestJson,
                        bundle.RenderedContext,
                        bundle.BundleChecksum,
                        projectContext,
                        ReasonCodeTranslations),
                    JsonOptions);
            }
            var thinkingStartedAt = clock.UtcNow;
            await ReportAsync(
                ChiefTurnActivity.Thinking,
                activityStartedAt: thinkingStartedAt);
            var communicationInstructions =
                (await leadershipProfile.ReadAsync(cancellationToken)).CommunicationInstructions;
            var specialists = await ReadSpecialistCatalogAsync(lease.Turn.TenantId, cancellationToken);
            communicationContext = await ResolveCommunicationContextAsync(lease, cancellationToken);
            AgentExecutionResult execution;
            using (var invocationActivity = PoseidonTelemetry.StartChiefInvocation(
                       lease.Turn.Selection?.Source,
                       lease.Turn.Selection?.ModelName))
            {
                using var heartbeatCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeat = MaintainActivityHeartbeatAsync(
                    ChiefTurnActivity.Thinking,
                    thinkingStartedAt,
                    leaseLost,
                    heartbeatCancellation.Token);
                try
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
                            specialists,
                            communicationContext),
                        // Perder o lease ABORTA a inferência: seguir gastando cota para um turno
                        // que já pertence a outro dono é o custo duplicado que BR-005 descreve.
                        leaseLost.Token);
                }
                finally
                {
                    await heartbeatCancellation.CancelAsync();
                    await heartbeat;
                }
                invocationActivity?.SetTag("gen_ai.response.model", lease.Turn.Selection?.ModelName);
                invocationActivity?.SetTag("gen_ai.client.operation.duration_ms", execution.DurationMs);
                invocationActivity?.SetTag("agent.executor", execution.Executor);
            }
            var output = ChiefTurnOutputContract.Parse(execution.StructuredOutput);
            if (output.TeamActions is { Count: > 0 } &&
                ChiefTeamActionResponse.ContainsPrematureCompletionClaim(output.Response))
            {
                throw new AgentOutputValidationException(
                    "Chief response announced a team change before the Control Plane confirmed it.");
            }
            if (!ChiefCommunicationPolicy.TryValidateResponse(
                    output.Response, communicationContext, out var communicationViolation))
            {
                throw new AgentOutputValidationException(
                    communicationViolation ?? "Chief communication policy rejected the response.");
            }
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
            // B14 (Fase 2B): a rota da intenção decide o que este turno PODE fazer. Sem este
            // portão a taxonomia seria decoração: o modelo diria "conversa_geral" e, se ainda
            // assim emitisse demandas, elas entrariam no board do dono como trabalho que ninguém
            // pediu. O corte é registrado — nunca silencioso.
            var gated = ChiefIntentGate.Apply(output);
            output = gated.Output;
            Observability.PoseidonTelemetry.RecordChiefIntent(
                ChiefIntentDispatchTable.Name(gated.Route.Intent),
                gated.AnythingDropped,
                execution.DurationMs);
            if (gated.AnythingDropped)
            {
                await governance.AppendMetricAsync(
                    Metric(lease, GovernanceMetricKind.EvaluatorVerdict, null, null,
                        $"intent.actions_dropped:{gated.DropReason}", clock.UtcNow),
                    cancellationToken);
            }

            // O turno é o laço mais quente e era o ÚNICO caminho de execução sem registro de
            // custo ou duração: `model_invocations` cobre os runs de especialista, e o turno da
            // chefe não escrevia lá nem em lugar nenhum. Falha aqui não derruba o turno — perder a
            // medição é ruim, perder a resposta ao dono é pior.
            try
            {
                await turnIntents.RecordAsync(
                    new ChiefTurnIntentRecord(
                        lease.Turn.TenantId,
                        lease.Turn.ProjectId,
                        lease.Turn.TurnId,
                        ChiefIntentDispatchTable.Name(gated.Route.Intent),
                        output.IntentConfidence,
                        gated.DemandsDropped,
                        gated.TeamActionsDropped,
                        execution.DurationMs,
                        clock.UtcNow),
                    cancellationToken);
            }
            catch (Exception measurementFailure) when (measurementFailure is not OperationCanceledException)
            {
                _ = measurementFailure;
                Observability.PoseidonTelemetry.RecordChiefIntent("record_failed", false, 0);
            }

            if (gated.Route.Intent == ChiefTurnIntent.Unmatched)
            {
                // Taxonomia incompleta é dado, não erro: o evento existe para expandi-la com
                // mensagens reais em vez de adivinhar categorias em mesa de reunião.
                await governance.AppendMetricAsync(
                    Metric(lease, GovernanceMetricKind.EvaluatorVerdict, null, null,
                        $"intent.unmatched:confidence={output.IntentConfidence:F2}", clock.UtcNow),
                    cancellationToken);
            }

            IReadOnlyList<ChiefTeamActionResult> teamActionResults = [];
            if (output.TeamActions is { Count: > 0 } teamActions)
            {
                teamActionResults = await ApplyTeamActionsAsync(
                    lease, teamActions, cancellationToken);
            }
            else if (gated.TeamActionsDropped > 0)
            {
                teamActionResults =
                [new ChiefTeamActionResult(
                    "intent_gate", "team.intent_disallowed", null, false)];
            }

            // OPS-024: o laço de escalação só fecha se a chefe EMITIR a ação. Estes dois números
            // são a fronteira exata do diagnóstico — quantos cards escalados foram ao contexto e
            // quantas ações voltaram na saída. Com eles, "ela não obedeceu" se separa de "ela não
            // recebeu" em uma leitura de log, sem reinstrumentar nada.
            LogChiefCardActionSignal(
                logger,
                lease.Turn.TurnId,
                projectContext?.EscalatedCards?.Count ?? 0,
                output.CardActions?.Count ?? 0);

            // A decisão do dono sobre um card ESCALADO precisa virar transição de estado, não só
            // texto. Sem isto o laço de escalação ficava aberto: a Bruna chamava o dono, ele
            // respondia reduzindo o escopo, ela confirmava — e o card seguia escalado, enquanto
            // ela anunciava que "essa parte volta a andar". Progresso relatado sem progresso real
            // é a pior falha possível para quem confia no sistema de longe.
            if (output.CardActions is { Count: > 0 } cardActions)
            {
                await ApplyCardActionsAsync(lease, cardActions, cancellationToken);
            }

            output = output with
            {
                Response = ChiefTeamActionResponse.Project(output.Response, teamActionResults),
            };
            if (!ChiefCommunicationPolicy.TryValidateResponse(
                    output.Response, communicationContext, out communicationViolation))
            {
                throw new AgentOutputValidationException(
                    communicationViolation ?? "Chief communication policy rejected the projected response.");
            }

            // A única projeção que pode chegar ao canal é a resposta validada E confirmada pelos
            // efeitos. Chunks do adapter são dados não confiáveis e não podem contornar a policy.
            var chunks = new[] { output.Response };
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
            var lastProjectedActivityAt = occurredAt;
            if (demandSeeds.Length > 0)
            {
                // O Chefe está delegando: demandas serão materializadas para agentes.
                lastProjectedActivityAt = await ReportAsync(
                    ChiefTurnActivity.Delegating,
                    demandSeeds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // O outbox ordena por timestamp. A conclusão precisa ser estritamente posterior à
            // última atividade publicada; caso contrário, um relógio de baixa resolução pode
            // entregar `delegating` depois de `completed` e reabrir o spinner no resync do chat.
            var completionOccurredAt = clock.UtcNow;
            if (completionOccurredAt <= lastProjectedActivityAt)
            {
                completionOccurredAt = lastProjectedActivityAt.AddTicks(1);
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
                    completionOccurredAt,
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

            // A promessa de delegação da Bruna vira TRABALHO REAL: cada demanda semeada pelo turno
            // é decomposta em plano e materializada em cards do board (backlog), de onde a triagem
            // por ondas + o loop autônomo assumem.
            //
            // Fase 0A1 (BR-004): isso NÃO acontece mais aqui. Materializar em memória depois do
            // commit deixava uma janela em que o turno já aparecia concluído para o usuário e a
            // demanda ainda não tinha plano nem cards — uma queda ali perdia o trabalho para
            // sempre, em silêncio. Agora o compromisso é gravado na MESMA transação que conclui o
            // turno (registro `demand_materializations` + comando `plan.materializationRequested`
            // na outbox) e executado por um consumidor durável com lease, fencing e reconciliação.

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
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            // Fencing perdido. NÃO escrevemos nada — nem conclusão, nem falha: o store recusaria,
            // e registrar uma falha aqui marcaria como quebrado um turno que outro dono está
            // conduzindo normalmente. O fato fica no log e na métrica.
            turnActivity?.SetTag("chief.result", "lease_lost");
            PoseidonTelemetry.RecordChiefTurn(
                "lease_lost",
                Stopwatch.GetElapsedTime(turnStartedAt).TotalMilliseconds);
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
            // A MENSAGEM entra no log, não só o nome do tipo. Um turno que morre por validação
            // de contrato dizia apenas "AgentOutputValidationException", e descobrir QUAL campo o
            // modelo errou virava tentativa e erro com o Host reiniciando a cada palpite.
            LogTurnFailure(
                logger,
                lease.Turn.TurnId,
                exception.GetType().Name,
                retryable,
                exception.Message);

            // O turno morreu: o usuário perguntou e NINGUÉM ia responder. Até aqui o fato ficava
            // só no mailbox e no log — do lado de fora, a conversa simplesmente parava, sem
            // resposta e sem erro. Quem responde pelo projeto é a Bruna, então é ela quem conta a
            // má notícia em linguagem de negócio; o diagnóstico permanece somente na auditoria.
            if (outcome.Terminal)
            {
                await AnnounceTerminalFailureAsync(
                    lease, communicationContext, cancellationToken);
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
    /// Aplica as intenções de gestão de equipe ANTES de confirmar o turno. Uma ação recusada não
    /// derruba a conversa, mas vira resultado explícito para a resposta: a Bruna nunca anuncia
    /// uma pessoa que só existiu no texto do modelo.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed team action must not poison the completed turn.")]
    /// <summary>
    /// Aplica a decisão do dono sobre cards escalados.
    ///
    /// A instrução que ele deu SUBSTITUI o enunciado do card e o replanejamento devolve o card à
    /// fila. Quem valida estado e versão é a cadeia — um card que não está escalado, ou que
    /// pertence a outro projeto, é recusado ali, não aqui. Uma falha isolada não derruba o turno:
    /// a resposta ao dono já foi produzida e o registro do que não pôde ser aplicado fica no log.
    /// </summary>
    private async Task ApplyCardActionsAsync(
        ChiefTurnLease lease,
        IReadOnlyList<ChiefCardAction> actions,
        CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var board = scope.ServiceProvider.GetRequiredService<IWorkBoardStore>();
        var chain = scope.ServiceProvider.GetRequiredService<IWorkChainStore>();

        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var task = await board.GetTaskAsync(lease.Turn.TenantId, action.CardId, cancellationToken);

                // O card precisa existir, pertencer a ESTE projeto e estar escalado. A checagem de
                // projeto não é formalidade: sem ela, um identificador inventado pelo modelo
                // alcançaria trabalho de outro projeto do mesmo tenant.
                if (task is null ||
                    !string.Equals(task.ProjectId, lease.Turn.ProjectId, StringComparison.Ordinal) ||
                    !string.Equals(task.InternalState, "escalated", StringComparison.Ordinal))
                {
                    LogCardActionRejected(logger, action.CardId, "card_not_escalated_in_project");
                    continue;
                }

                var now = clock.UtcNow;
                var contentHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(action.Instruction)));
                var receipt = await chain.ReplanEscalatedTaskAsync(
                    new WorkTaskReplanCommand(
                        lease.Turn.TenantId,
                        task.BackingSolicitationId,
                        task.Id,
                        UlidValue.New(now).ToString(),
                        action.Instruction,
                        contentHash,
                        lease.Turn.ProjectId,
                        "owner.decision_after_escalation",
                        $"turn:{lease.Turn.TurnId}",
                        task.Version,
                        // A versão entra na chave porque o inbox guarda também mutações RECUSADAS:
                        // uma chave fixa envenenaria toda tentativa posterior com conflito.
                        $"chief-turn-replan:{task.Id}:v{task.Version}:{contentHash[..12]}",
                        now),
                    cancellationToken);

                LogCardActionApplied(logger, task.Id, receipt.Status);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCardActionRejected(logger, action.CardId, exception.GetType().Name);
            }
        }
    }

    private async Task<IReadOnlyList<ChiefTeamActionResult>> ApplyTeamActionsAsync(
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
                return actions.Select(action => new ChiefTeamActionResult(
                    action.Action, "team.profile_missing", action.PersonaKey, false)).ToArray();
            }

            var results = await teamManager.ApplyAsync(
                lease.Turn.TenantId, lease.Turn.ProjectId, profile.Id, actions, cancellationToken);
            foreach (var result in results)
            {
                LogTeamAction(logger, lease.Turn.TurnId, result.Action, result.ReasonCode);
            }

            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogTeamActionFailure(logger, lease.Turn.TurnId, exception.GetType().Name);
            return actions.Select(action => new ChiefTeamActionResult(
                action.Action, "team.execution_failed", action.PersonaKey, false)).ToArray();
        }
    }

    /// <summary>
    /// Publica, na própria conversa, que a mensagem do usuário NÃO foi respondida. É o único
    /// lugar em que a falha terminal vira informação para quem perguntou; sem isto a conversa
    /// morre em silêncio e o usuário fica esperando uma resposta que não vem.
    ///
    /// A mensagem é de Bruna (a única voz com o usuário) e explica o próximo passo sem expor
    /// código, identificador, prompt ou resposta parcial. Uma falha AQUI não pode mascarar a
    /// falha original, então nada é propagado.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Announcing the failure must never mask the failure being announced.")]
    private async Task AnnounceTerminalFailureAsync(
        ChiefTurnLease lease,
        ChiefCommunicationContext communicationContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var occurredAt = clock.UtcNow;
            var content = ChiefCommunicationPolicy.TerminalFailureMessage(communicationContext);
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
    /// Resolve a projeção fora do modelo. A mensagem pode pedir detalhes, mas só um perfil do
    /// mesmo tenant com papel administrativo ou entitlement explícito os libera.
    /// </summary>
    private async Task<ChiefCommunicationContext> ResolveCommunicationContextAsync(
        ChiefTurnLease lease,
        CancellationToken cancellationToken)
    {
        var requested = ChiefCommunicationPolicy.RequestsTechnicalDetails(lease.Instruction);
        if (!requested)
        {
            return ChiefCommunicationPolicy.Business;
        }

        var userMessage = await conversations.GetMessageAsync(
            lease.Turn.TenantId, lease.Turn.UserMessageId, cancellationToken);
        if (userMessage?.AuthorProfileId is not { Length: > 0 } profileId)
        {
            return new ChiefCommunicationContext(TechnicalDetailsRequested: true);
        }

        var profile = await localProfiles.GetAsync(profileId, cancellationToken);
        if (profile is null ||
            !string.Equals(profile.TenantId, lease.Turn.TenantId, StringComparison.Ordinal))
        {
            return new ChiefCommunicationContext(TechnicalDetailsRequested: true);
        }

        if (profile.Role == LocalProfileRole.Admin)
        {
            return new ChiefCommunicationContext(
                TechnicalDetailsRequested: true,
                TechnicalDetailsAuthorized: true);
        }

        var entitlements = await licenses.ListEntitlementsAsync(
            lease.Turn.TenantId, null, 100, cancellationToken);
        var authorized = entitlements.Any(entitlement =>
            entitlement.Included &&
            string.Equals(
                entitlement.Key,
                ChiefCommunicationPolicy.RequiredTechnicalEntitlement,
                StringComparison.Ordinal));
        return new ChiefCommunicationContext(
            TechnicalDetailsRequested: true,
            TechnicalDetailsAuthorized: authorized);
    }

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
        string BundleChecksum,
        ChiefProjectContext? Project,
        IReadOnlyDictionary<string, string> ReasonCodeTranslations);

    private sealed record ChiefGovernanceContextWithMemory(
        string StatusDigestJson,
        string ContextBundle,
        string BundleChecksum,
        ChiefProjectContext? Project,
        IReadOnlyDictionary<string, string> ReasonCodeTranslations,
        string ChiefContext,
        int PersistedNoteCount);

    [LoggerMessage(
        EventId = 2109,
        Level = LogLevel.Warning,
        Message = "Chief: heartbeat de atividade do turno {TurnId} falhou com {ErrorType}; a execução continua.")]
    private static partial void LogActivityHeartbeatFailure(
        ILogger logger,
        string turnId,
        string errorType);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Chief turn {TurnId} failed with {ErrorType}; retryable={Retryable}. {Detail}")]
    private static partial void LogTurnFailure(
        ILogger logger,
        string turnId,
        string errorType,
        bool retryable,
        string detail);

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
        EventId = 2107,
        Level = LogLevel.Warning,
        Message = "Chief: o turno {TurnId} perdeu o lease (fencing {FencingToken}); a execução foi abortada sem escrever.")]
    private static partial void LogTurnLeaseLost(
        ILogger logger, string turnId, long fencingToken);

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
        EventId = 2108,
        Level = LogLevel.Information,
        Message = "Chief: decisão do dono replanejou o card {TaskId} ({Status}).")]
    private static partial void LogCardActionApplied(
        ILogger logger, string taskId, WorkChainMutationStatus status);

    [LoggerMessage(
        EventId = 2110,
        Level = LogLevel.Information,
        Message = "Chief: turno {TurnId} recebeu {EscalatedInContext} card(s) escalado(s) no contexto e emitiu {CardActionsEmitted} cardAction(s).")]
    private static partial void LogChiefCardActionSignal(
        ILogger logger, string turnId, int escalatedInContext, int cardActionsEmitted);

    [LoggerMessage(
        EventId = 2111,
        Level = LogLevel.Warning,
        Message = "Chief: decisão do dono sobre o card {CardId} NÃO foi aplicada ({Reason}).")]
    private static partial void LogCardActionRejected(ILogger logger, string cardId, string reason);

    [LoggerMessage(
        EventId = 2105,
        Level = LogLevel.Error,
        Message = "Chief: falha terminal do turno {TurnId} NÃO pôde ser anunciada ao usuário ({ErrorType}).")]
    private static partial void LogTerminalAnnouncementFailure(
        ILogger logger, string turnId, string errorType);
}
