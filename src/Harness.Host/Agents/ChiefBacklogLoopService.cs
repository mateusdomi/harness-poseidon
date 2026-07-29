using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Modules.Providers.Application;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Agents;

/// <summary>
/// Loop autônomo do CHEFE (o coração da orquestração): a cada ciclo drena o backlog de CARDS
/// (`Ready`), decide qual card vai para qual conta disponível (<see cref="ChiefBacklogPolicy"/>),
/// compõe o briefing PERSONA + CARD (<see cref="PersonaCardComposer"/>) e delega ao agente
/// especializado via o orquestrador — respeitando cota/cooldown (ledger durável), concorrência
/// e um teto de despacho.
///
/// Nasce DESLIGADO (<c>AutoDispatch:Enabled=false</c>): um auto-dispatch executa agentes reais
/// e gasta cota, então só roda quando o operador o habilita. NUNCA publica em `develop` sozinho
/// — o card vai só até o resultado/`AwaitingReview`; a integração é um gate humano separado.
/// </summary>
public sealed partial class ChiefBacklogLoopService(
    IServiceScopeFactory scopes,
    AgentRunOrchestrator orchestrator,
    AgentAccountRegistry accounts,
    AccountAvailabilityLedger availability,
    ChiefBacklogPolicy policy,
    AgentRunSettings settings,
    IClock clock,
    CapacityManager capacity,
    ProviderRoutingCoordinator providerRouting,
    ILogger<ChiefBacklogLoopService> logger) : BackgroundService
{
    /// <summary>Despachante em escala (Fase 10) — puro e determinístico, um por processo.</summary>
    private static readonly ScaleDispatcher ScaleGate = new();

    /// <summary>
    /// Traduz o estado do Capacity Manager (Fase 3) em sinal de cota para o plano de despacho:
    /// conta com circuito aberto por falhas consecutivas, ou marcada como esgotada com janela de
    /// volta, não recebe card nesta rodada. A confiança é ALTA porque o fato é local e medido —
    /// nenhuma fração é inventada.
    /// </summary>
    private Dictionary<string, AccountQuotaSnapshot> CapacitySignals(DateTimeOffset now)
    {
        var signals = new Dictionary<string, AccountQuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts.List())
        {
            var snapshot = capacity.GetQuotaSnapshot(account.Alias, now);
            var tripped = capacity.IsCircuitTripped(account.Alias, now);
            var exhausted = string.Equals(snapshot.Status, "Exhausted", StringComparison.OrdinalIgnoreCase) &&
                snapshot.ResetAt is { } reset && reset > now;
            if (!tripped && !exhausted)
            {
                continue;
            }

            signals[account.Alias] = new AccountQuotaSnapshot(
                "capacity-manager",
                snapshot.ObservedAt,
                QuotaStatus.Exhausted,
                QuotaConfidence.High,
                null,
                snapshot.ResetAt,
                snapshot.StaleAfter,
                snapshot.OverrideReason);
        }

        return signals;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief backlog loop DESLIGADO (AutoDispatch:Enabled=false).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: {Dispatched} card(s) despachado(s), {Deferred} adiado(s).")]
    private static partial void LogCycle(ILogger logger, int dispatched, int deferred);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ciclo do chefe falhou: {ErrorType}")]
    private static partial void LogFailure(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: tentativa NÃO iniciada para o card {TaskId}: {Status}")]
    private static partial void LogAttemptNotStarted(ILogger logger, string taskId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: run REJEITADO para o card {TaskId}: {Status}/{Code}")]
    private static partial void LogRunRejected(ILogger logger, string taskId, string status, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} pulado — o papel '{Role}' não possui escopo de escrita; despachá-lo seria rejeitado por agent_path_scope_empty a cada ciclo.")]
    private static partial void LogCardWithoutWriteScope(ILogger logger, string taskId, string role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} (card_type={CardType}) NÃO despachável — pulado por prontidão (DoR): {Blockers}")]
    private static partial void LogCardNotDispatchable(ILogger logger, string taskId, string cardType, string blockers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} com CIRCUITO ABERTO ({Failures} falhas consecutivas) — não é redespachado; só o replanejamento da Bruna o reabre.")]
    private static partial void LogCardCircuitOpen(ILogger logger, string taskId, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: a especialidade '{PersonaKey}' pedida pelo card {TaskId} não existe como especialista habilitado no catálogo; usando o fallback inferido.")]
    private static partial void LogPersonaNotInCatalog(ILogger logger, string taskId, string personaKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: a persona '{PersonaKey}' existe mas não é elegível para o card {TaskId}; usando o fallback inferido.")]
    private static partial void LogPersonaNotEligible(ILogger logger, string taskId, string personaKey);

    /// <summary>
    /// Procura a persona pela chave EXIGINDO que ela seja um especialista habilitado. É aqui que a
    /// declaração do Chefe deixa de ser texto e passa a valer (ou não): o catálogo é a autoridade.
    /// </summary>
    private static AgentDefinitionRecord? FindPersona(
        IReadOnlyList<AgentDefinitionRecord> personas, string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : personas.FirstOrDefault(definition =>
                definition.Enabled &&
                definition.ArchivedAt is null &&
                string.Equals(definition.Role, "specialist", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(definition.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A persona encontrada pode REALMENTE assumir este card? Criar não é o mesmo que executar:
    /// uma persona em quarentena, presa a outro projeto ou acima da faixa de risco autorizada
    /// aparenta capacidade que não tem, e delegar-lhe o card gastaria cota para falhar depois.
    /// </summary>
    private static bool IsEligible(
        AgentDefinitionRecord persona, string projectId, string cardRiskTier) =>
        PersonaEligibilityPolicy.Evaluate(
            new PersonaEligibilityInput(
                persona.Key,
                persona.Role,
                persona.Enabled,
                persona.ArchivedAt is not null,
                persona.LifecycleState,
                persona.ScopeProjectId,
                persona.Risk,
                persona.ToolIds ?? [],
                MissingRequiredCapabilities: []),
            projectId,
            cardRiskTier,
            // Ferramenta ainda não é resolvida por card neste ponto do laço: exigi-la aqui
            // recusaria toda persona do catálogo semeado, que nasce sem tool.
            requiresTools: false).Eligible;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.AutoDispatchEnabled)
        {
            LogDisabled(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (dispatched, deferred) = await RunCycleAsync(stoppingToken);
                if (dispatched > 0 || deferred > 0)
                {
                    LogCycle(logger, dispatched, deferred);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailure(logger, exception.GetType().Name);
            }

            try
            {
                await Task.Delay(settings.AutoDispatchInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // `internal` (não `private`) para a prova determinística de dispatch: um teste roda UM ciclo
    // e inspeciona o par (Dispatched, Deferred) — ver InternalsVisibleTo no .csproj.
    internal async Task<(int Dispatched, int Deferred)> RunCycleAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var profiles = scope.ServiceProvider.GetRequiredService<ILocalProfileStore>();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var board = scope.ServiceProvider.GetRequiredService<IWorkBoardStore>();
        var chain = scope.ServiceProvider.GetRequiredService<IWorkChainStore>();
        var catalog = scope.ServiceProvider.GetRequiredService<IAgentCatalogStore>();
        var circuits = new CardCircuitBreakerService(
            scope.ServiceProvider.GetRequiredService<ICardCircuitBreakerStore>());

        var profileList = await profiles.ListAsync(token);
        if (profileList.Count == 0)
        {
            return (0, 0);
        }

        var profile = profileList[0];

        var controlledRoot = System.IO.Path.GetFullPath(settings.ControlledRoot!);
        var personas = await catalog.ListDefinitionsForTenantAsync(profile.TenantId, null, 100, false, token);
        var plans = scope.ServiceProvider.GetRequiredService<IDemandPlanStore>();

        var dispatched = 0;
        var deferred = 0;

        var projectList = await projects.ListAsync(profile.TenantId, null, 50, token);
        foreach (var project in projectList)
        {
            // `pause` do chefe precisa PARAR de verdade. Sem este filtro o loop continuava
            // colhendo, revisando e despachando cards de projeto pausado/arquivado — o botão
            // existia na API e não segurava nada, e um projeto que o dono mandou parar seguia
            // gastando cota e slot de despacho dos projetos ativos.
            if (!IsDispatchable(project))
            {
                continue;
            }

            if (!IsInsideControlledRoot(project, controlledRoot))
            {
                continue;
            }

            // ACOMPANHAMENTO do chefe (antes de despachar novos cards): 1) colher runs
            // concluídos — a tentativa vira `awaiting_review` e o card vai a `review`; 2) o code
            // review acontece por OUTRO agente (ator≠crítico) e o veredito é aplicado na cadeia;
            // 3) triagem por ondas — cards de plano com DoR ok e dependências ENTREGUES sobem de
            // `backlog` para `ready`, de onde o despacho abaixo os assume. É este trio que fecha
            // o ciclo "um agente termina → o chefe confere → o próximo card entra". Uma falha
            // aqui é logada e NUNCA impede o despacho do restante do ciclo.
            try
            {
                await HarvestCompletedRunsAsync(profile.TenantId, project, controlledRoot, board, chain, token);
                await ReviewAwaitingAttemptsAsync(profile.TenantId, project, controlledRoot, board, chain, token);
                await PrepareCorrectionsAsync(profile.TenantId, project, board, chain, token);
                await ResolveAgentRequestsAsync(profile.TenantId, project, scope, token);
                await IntegrateApprovedCardsAsync(profile.TenantId, project, board, scope, token);
                await AnnounceEscalatedCardsAsync(profile.TenantId, project, board, scope, token);
                await DrivePhaseAsync(profile.TenantId, profile.Id, project, scope, token);
                await PromotePlannedCardsAsync(profile.TenantId, project, board, plans, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFollowUpFailure(logger, project.Id, exception.GetType().Name);
            }

            // Cards prontos para delegar: board_state `ready` (minúsculo — o enum é case-sensitive
            // no SQLite), não arquivados. Cards nascem em `backlog`; a triagem (humano/DoR) promove
            // a `ready` antes de o loop os enxergar.
            var page = await board.PageTasksAsync(
                profile.TenantId,
                new BoardTaskPageQuery(project.Id, null, null, "ready", null, null, "active", null, 0, 50),
                token);

            // Mapa das superfícies REAIS do repositório do projeto, lido uma vez por ciclo. É ele
            // que permite ao card reivindicar o módulo que ele mexe em vez de `src/**` inteiro —
            // sem isso, dois cards independentes do mesmo projeto nunca rodam juntos.
            var surfaceMap = string.IsNullOrWhiteSpace(project.RepositoryUrl)
                ? RepositorySurfaceMap.Empty
                : RepositorySurfaceMap.Build(System.IO.Path.GetFullPath(project.RepositoryUrl));

            var cards = new List<(ChiefCard Card, ChiefCardResolution Resolution, BoardTaskRecord Task, string InstructionVersionId)>();
            foreach (var task in page.Items)
            {
                // Card recusado na largada há pouco (conflito de claim): esperar o escopo liberar é
                // a decisão correta — re-tentar em seguida só produz tentativa fantasma.
                if (_dispatchBackoff.TryGetValue(task.Id, out var retryAt) && retryAt > clock.UtcNow)
                {
                    continue;
                }

                var instructions = await board.ListInstructionsAsync(profile.TenantId, task.Id, null, 50, token);

                // Gate fail-safe da Definition of Ready: SÓ cards 'agent_task' com instrução e não
                // bloqueados entram na fila de despacho. 'human_gate'/'decision'/'feature'/'spike'
                // NUNCA são auto-despachados — mesmo já em `ready`, são pulados aqui com bloqueador
                // tipado (a triagem/humano cuida deles fora do loop).
                var readiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
                    task.CardType,
                    instructions.Count >= 1,
                    string.Equals(task.State, "blocked", StringComparison.Ordinal) ||
                        !string.IsNullOrWhiteSpace(task.BlockedReason)));
                if (!readiness.IsDispatchable)
                {
                    LogCardNotDispatchable(
                        logger, task.Id, task.CardType, string.Join(",", readiness.Blockers));
                    continue;
                }

                // CIRCUITO DO CARD (B4): quando o mesmo card falha três vezes seguidas, o defeito
                // está no enunciado, não no agente — redespachar é repetir o fracasso queimando
                // cota. O circuito é recalculado do histórico de tentativas (idempotente, imune a
                // reinício) e só o replanejamento da Bruna o reabre. Sem isto, o único freio era a
                // escalação por ciclos de review, que não cobre falha de execução.
                var attemptHistory = await board.ListAttemptsAsync(profile.TenantId, task.Id, null, 100, token);
                var circuit = await circuits.SynchronizeAsync(
                    profile.TenantId,
                    project.Id,
                    task.Id,
                    [.. attemptHistory.Select(attempt =>
                        (attempt.State, attempt.FinishedAt ?? attempt.StartedAt))],
                    token);
                if (!circuit.IsDispatchable)
                {
                    LogCardCircuitOpen(logger, task.Id, circuit.ConsecutiveFailures);
                    continue;
                }

                var resolution = ChiefCardResolver.Resolve(
                    task.Title, instructions[^1].Body, [], "medium", surfaceMap: surfaceMap);

                // Defesa em profundidade: um papel SEM escopo de escrita (o crítico, por exemplo)
                // produz claim vazia, e a política de path rejeita a tentativa com
                // `agent_path_scope_empty`. Sem esta guarda o loop redespachava o mesmo card a cada
                // ciclo, para sempre, queimando slot e registrando tentativa rejeitada sem NUNCA
                // avisar ninguém. O card é pulado com motivo tipado — quem decide o que fazer com
                // ele é a triagem, não um retry cego.
                if (resolution.ScopeClaims.Count == 0)
                {
                    LogCardWithoutWriteScope(logger, task.Id, resolution.Role);
                    continue;
                }

                cards.Add((
                    new ChiefCard(task.Id, project.Id, resolution.Role, resolution.RequiredCapability,
                        PriorityWeight(task.Priority), resolution.ScopeClaims),
                    resolution, task, instructions[^1].Id));
            }

            if (cards.Count == 0)
            {
                continue;
            }

            // Fase 10 — despacho em ESCALA: antes de escolher contas, a fila priorizada corta
            // pelo teto GLOBAL VIVO (runs em andamento agora + já despachados nesta rodada, em
            // todos os projetos). Sem isto, cada rodada enxergaria só o próprio orçamento e o
            // processo ultrapassaria o teto com runs de rodadas anteriores ainda vivos.
            var queue = new CardPrioritizedBuffer();
            foreach (var candidate in cards)
            {
                queue.Enqueue(
                    candidate.Card.TaskId,
                    candidate.Card.Role,
                    candidate.Card.Priority >= 70
                        ? CardPriority.High
                        : candidate.Card.Priority >= 40 ? CardPriority.Normal : CardPriority.Low,
                    clock.UtcNow);
            }

            var scale = ScaleGate.Dispatch(
                queue,
                Math.Max(1, settings.AutoDispatchMaxConcurrent),
                orchestrator.LiveRunCount + dispatched);
            var admitted = new HashSet<string>(
                scale.DispatchedWorkerCards.Concat(scale.DispatchedCriticCards),
                StringComparer.Ordinal);
            deferred += scale.DeferredCount;
            if (admitted.Count == 0)
            {
                continue;
            }

            var planningCards = cards
                .Where(entry => admitted.Contains(entry.Card.TaskId))
                .Select(entry => entry.Card)
                .ToArray();
            var routingNow = clock.UtcNow;
            await providerRouting.RefreshCapacityAsync(
                profile.TenantId, planningCards, routingNow, token);
            var plan = policy.Plan(
                planningCards,
                accounts, availability,
                settings.AutoDispatchMaxConcurrent, routingNow,
                CapacitySignals(routingNow));
            deferred += plan.Deferred.Count;
            foreach (var deferral in plan.Deferred)
            {
                // O MOTIVO tipado do adiamento é operável (conta indisponível? escopo? cota?);
                // sem ele o operador só vê o contador e não consegue agir. O detalhe por conta
                // (candidatos do scheduler) diz exatamente QUEM foi recusado e POR QUÊ.
                LogCardDeferred(
                    logger, deferral.Card.TaskId, deferral.ReasonCode,
                    deferral.RetryAfter?.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                    deferral.Candidates is null
                        ? "-"
                        : string.Join(", ", deferral.Candidates.Select(candidate =>
                            $"{candidate.Alias}:{candidate.ReasonCode}")));
            }

            // Adiamento transitório é rotina e não incomoda ninguém. Adiamento ESTRUTURAL é outra
            // coisa: nenhuma espera o resolve, e o dono precisa saber que aquele card não tem quem
            // o execute — senão ele fica parado para sempre com o fato vivo só no log.
            await AnnounceUndispatchableCardsAsync(
                profile.TenantId, project, cards, plan.Deferred, scope, token);

            foreach (var decision in plan.Dispatch)
            {
                var entry = cards.First(candidate => candidate.Card.TaskId == decision.Card.TaskId);
                var routing = await providerRouting.RouteAndAuditAsync(
                    profile.TenantId,
                    project.Id,
                    decision,
                    preferredModel: null,
                    routingNow,
                    token);
                if (await LaunchAsync(
                        profile.TenantId, project, entry.Resolution, decision.AccountAlias,
                        routing.SelectedModel, entry.Task, entry.InstructionVersionId, personas,
                        controlledRoot,
                        string.Equals(
                            decision.ReasonCode, "chief.reinforcement_dispatched", StringComparison.Ordinal),
                        board, chain, token))
                {
                    dispatched++;
                }
            }
        }

        return (dispatched, deferred);
    }

    /// <summary>
    /// Vereditos de review que representam uma REVISÃO real executada (e portanto podem ser
    /// aplicados na cadeia). Qualquer outro código é falha de infraestrutura/conta e vira
    /// retry com backoff — nunca rejeita o trabalho do ator por culpa do crítico.
    /// </summary>
    private static readonly HashSet<string> AppliableReviewReasons = new(
        ["critic.pass", "critic.fail", "critic.pass_contradicted_by_findings"],
        StringComparer.Ordinal);

    private static readonly TimeSpan ReviewRetryBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Backoff em memória por tentativa para reviews com falha de infraestrutura.</summary>
    private readonly Dictionary<string, DateTimeOffset> _reviewBackoff = new(StringComparer.Ordinal);

    /// <summary>
    /// Espera antes de re-tentar um card cujo run foi RECUSADO na largada (tipicamente conflito de
    /// claim com um run vivo do mesmo escopo). Sem ela, o ciclo re-despachava o card a cada
    /// intervalo do loop: cada rodada abria uma tentativa durável, colhia a recusa, expirava a
    /// lease e recomeçava — dezenas de tentativas fantasma na cadeia, sem nenhum trabalho feito.
    /// O escopo só libera quando o run concorrente termina, o que leva minutos, não segundos.
    /// </summary>
    private static readonly TimeSpan RejectedDispatchBackoff = TimeSpan.FromMinutes(2);

    /// <summary>Backoff em memória por CARD para despachos recusados na largada.</summary>
    private readonly Dictionary<string, DateTimeOffset> _dispatchBackoff = new(StringComparer.Ordinal);

    /// <summary>
    /// Marcador estável do aviso de escalação. É o que permite reconhecer, na própria conversa,
    /// que aquele card JÁ foi levado ao dono — idempotência que sobrevive a restart.
    /// </summary>
    private const string EscalationMarker = "Preciso da sua decisão em um card:";

    /// <summary>Cards cuja escalação já foi anunciada ao dono — o aviso é uma vez, não a cada ciclo.</summary>
    private readonly HashSet<string> _announcedEscalations = new(StringComparer.Ordinal);

    /// <summary>
    /// Marcador estável do aviso de card SEM EXECUTOR POSSÍVEL. Mesma função do marcador de
    /// escalação: reconhecer na conversa o que já foi dito, sobrevivendo a restart.
    /// </summary>
    private const string UndispatchableMarker = "Um card não tem quem o execute:";

    /// <summary>Cards já anunciados como indespacháveis nesta instância do processo.</summary>
    private readonly HashSet<string> _announcedUndispatchable = new(StringComparer.Ordinal);

    /// <summary>Fases cujo portão já foi reportado como pronto para a decisão humana.</summary>
    private readonly HashSet<string> _announcedGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Elo de COLHEITA: um run externo que terminou não fecha sozinho a cadeia durável. Aqui o
    /// chefe confere cada card em `development`: run Completed → commit de colheita dos restos da
    /// worktree (se houver) e <c>CompleteAttemptAsync</c> (tentativa `awaiting_review`, card em
    /// `review`); run Failed/Cancelled → <c>ExpireAttemptLeaseAsync</c> devolve o card a `ready`
    /// para re-despacho. Idempotente por tentativa.
    /// </summary>
    internal async Task<int> HarvestCompletedRunsAsync(
        string tenantId,
        ProjectRecord project,
        string controlledRoot,
        IWorkBoardStore board,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var harvested = 0;
        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, "development", null, null, "active", null, 0, 50),
            token);
        foreach (var task in page.Items)
        {
            if (!string.Equals(task.InternalState, "running", StringComparison.Ordinal))
            {
                continue;
            }

            var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
            var running = attempts.FirstOrDefault(attempt =>
                string.Equals(attempt.State, "running", StringComparison.Ordinal));
            if (running is null)
            {
                continue;
            }

            var snapshot = await orchestrator.GetAsync(tenantId, running.Id, token);
            var now = clock.UtcNow;
            if (snapshot is null)
            {
                // Tentativa SEM workspace: o orquestrador nunca aceitou o run (órfã de uma
                // compensação perdida — ex.: processo caiu entre o start da cadeia e o aceite).
                // A janela de tolerância evita expirar um lançamento em curso deste mesmo ciclo.
                if (running.StartedAt < now.AddMinutes(-2))
                {
                    _ = await chain.ExpireAttemptLeaseAsync(
                        new WorkAttemptLeaseExpiredCommand(
                            tenantId, task.BackingSolicitationId, task.Id, running.Id, task.Version,
                            $"chief-loop-orphan:{running.Id}", now),
                        token);
                    LogRunRequeued(logger, task.Id, running.Id, "orphan");
                }

                continue;
            }

            if (snapshot.Status == AgentRunStatus.Completed)
            {
                var branch = $"task/agent-run-{running.Id.ToLowerInvariant()}";
                await TryHarvestWorktreeAsync(project, controlledRoot, running.Id, branch, token);
                var completed = await chain.CompleteAttemptAsync(
                    new WorkAttemptCompleteCommand(
                        tenantId, task.BackingSolicitationId, task.Id, running.Id, task.Version,
                        [
                            new WorkEvidenceInput(
                                UlidValue.New(now).ToString(), $"agent-run:{running.Id}"),
                            new WorkEvidenceInput(
                                UlidValue.New(now.AddTicks(1)).ToString(), $"git-branch:{branch}"),
                        ],
                        $"chief-loop-complete:{running.Id}", now),
                    token);
                if (completed.Status is WorkChainMutationStatus.Applied
                    or WorkChainMutationStatus.IdempotentReplay)
                {
                    harvested++;
                    LogRunHarvested(logger, task.Id, running.Id);
                }
            }
            else if (snapshot.Status is AgentRunStatus.Failed or AgentRunStatus.Cancelled)
            {
                // Tentativa morta: abandona e devolve o card à fila (`ready`) para re-despacho.
                _ = await chain.ExpireAttemptLeaseAsync(
                    new WorkAttemptLeaseExpiredCommand(
                        tenantId, task.BackingSolicitationId, task.Id, running.Id, task.Version,
                        $"chief-loop-expire:{running.Id}", now),
                    token);
                LogRunRequeued(logger, task.Id, running.Id, snapshot.Status.ToString());
            }

            // Accepted/Running → ainda em voo; nada a fazer neste ciclo.
        }

        return harvested;
    }

    /// <summary>
    /// Elo de CODE REVIEW: para cada card `awaiting_review`, o chefe convoca um crítico de conta
    /// DIFERENTE da do ator (papel `critic`), entrega o diff REAL da branch da tentativa e aplica
    /// o veredito na cadeia durável — aprovado segue para o gate humano de merge; reprovado vai a
    /// `corrections` (e estourando o limite de ciclos, escala). Falha de infraestrutura do review
    /// NUNCA reprova o trabalho: entra em backoff e o chefe tenta de novo.
    /// </summary>
    internal async Task<int> ReviewAwaitingAttemptsAsync(
        string tenantId,
        ProjectRecord project,
        string controlledRoot,
        IWorkBoardStore board,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var reviewed = 0;
        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, "review", null, null, "active", null, 0, 50),
            token);
        foreach (var task in page.Items)
        {
            if (!string.Equals(task.InternalState, "awaiting_review", StringComparison.Ordinal))
            {
                continue;
            }

            // O estado exposto do attempt é o OPERACIONAL ('completed' cobre submetido-a-review);
            // o discriminador da fase é o estado interno do card (awaiting_review), já checado.
            var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
            var awaiting = attempts.LastOrDefault(attempt =>
                string.Equals(attempt.State, "completed", StringComparison.Ordinal));
            if (awaiting is null)
            {
                continue;
            }

            var now = clock.UtcNow;
            if (_reviewBackoff.TryGetValue(awaiting.Id, out var notBefore) && notBefore > now)
            {
                continue;
            }

            var instructions = await board.ListInstructionsAsync(tenantId, task.Id, null, 50, token);
            if (instructions.Count == 0)
            {
                continue;
            }

            var resolution = ChiefCardResolver.Resolve(task.Title, instructions[^1].Body, [], "medium");
            var producerAlias = awaiting.AgentId;
            var criticAlias = SelectCriticAlias(producerAlias, now);
            if (criticAlias is null)
            {
                LogNoCriticAvailable(logger, task.Id, producerAlias);
                _reviewBackoff[awaiting.Id] = now.Add(ReviewRetryBackoff);
                continue;
            }

            var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl!);
            var branch = $"task/agent-run-{awaiting.Id.ToLowerInvariant()}";
            string diff;
            try
            {
                using var manager = await GitWorktreeManager.OpenAsync(
                    repositoryRoot, controlledRoot, token);
                diff = await manager.DiffBranchAsync("HEAD", branch, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogReviewInfrastructureFailure(
                    logger, task.Id, awaiting.Id, $"diff:{exception.GetType().Name}");
                _reviewBackoff[awaiting.Id] = now.Add(ReviewRetryBackoff);
                continue;
            }

            if (string.IsNullOrWhiteSpace(diff))
            {
                diff = "(diff vazio: a tentativa não introduziu mudanças sobre a base publicada)";
            }
            else if (diff.Length > 160_000)
            {
                diff = string.Concat(
                    diff.AsSpan(0, 160_000), "\n... (diff truncado para o review)");
            }

            // O review roda dentro do ciclo; um executor de crítico que TRAVE congelaria o loop
            // inteiro (colheita, correções, triagem e despacho). O teto local garante que o
            // ciclo sempre volta: estouro vira falha de infraestrutura com backoff, nunca
            // reprovação do ator.
            CriticReviewResult result;
            using (var reviewTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                reviewTimeout.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    result = await orchestrator.ReviewAsync(
                        new AgentCriticReviewCommand
                        {
                            AttemptId = awaiting.Id,
                            CriticAlias = criticAlias,
                            ActorAlias = producerAlias,
                            ReviewDirectory = repositoryRoot,
                            Diff = diff,
                            TestEvidence =
                                "(evidência de teste não coletada automaticamente; avalie pelo diff e pelo repositório)",
                            AcceptanceCriteria = resolution.Card.AcceptanceCriteria,
                            ScopeClaims = resolution.ScopeClaims,
                        },
                        reviewTimeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    LogReviewInfrastructureFailure(logger, task.Id, awaiting.Id, "critic.review_timeout");
                    _reviewBackoff[awaiting.Id] = clock.UtcNow.Add(ReviewRetryBackoff);
                    continue;
                }
            }

            if (await ApplyReviewVerdictAsync(tenantId, task, awaiting.Id, result, chain, token))
            {
                reviewed++;
                _reviewBackoff.Remove(awaiting.Id);
            }
            else
            {
                _reviewBackoff[awaiting.Id] = now.Add(ReviewRetryBackoff);
            }
        }

        return reviewed;
    }

    /// <summary>
    /// Aplica um veredito de review REAL na cadeia durável (aprovado → gate humano de merge;
    /// reprovado → `corrections`/escalação). Vereditos de infraestrutura (conta indisponível,
    /// executor falhou, saída inválida) NÃO são aplicados — devolvem <c>false</c> para o chamador
    /// re-tentar com backoff, sem punir o trabalho do ator pela falha do crítico.
    /// </summary>
    internal async Task<bool> ApplyReviewVerdictAsync(
        string tenantId,
        BoardTaskRecord task,
        string attemptId,
        CriticReviewResult result,
        IWorkChainStore chain,
        CancellationToken token)
    {
        if (!AppliableReviewReasons.Contains(result.ReasonCode))
        {
            LogReviewInfrastructureFailure(logger, task.Id, attemptId, result.ReasonCode);
            return false;
        }

        var decision = result.Approved ? "approved" : "rejected";
        var findingsSummary = result.Findings.Count == 0
            ? string.Empty
            : $" Achados: {string.Join("; ", result.Findings.Select(finding => $"[{finding.Severity}] {finding.Summary}"))}";
        var rationale = $"{result.Summary ?? result.ReasonCode}{findingsSummary}";
        if (rationale.Length > 4_000)
        {
            rationale = rationale[..4_000];
        }

        var applied = await chain.ReviewAttemptAsync(
            new WorkAttemptReviewCommand(
                tenantId, task.BackingSolicitationId, task.Id, attemptId, result.ReviewId,
                result.CriticAlias, decision, rationale, task.Version,
                $"chief-loop-review:{result.ReviewId}", clock.UtcNow),
            token);
        if (applied.Status is WorkChainMutationStatus.Applied
            or WorkChainMutationStatus.IdempotentReplay)
        {
            LogReviewApplied(logger, task.Id, attemptId, result.CriticAlias, decision);
            return true;
        }

        LogReviewInfrastructureFailure(logger, task.Id, attemptId, $"chain:{applied.Status}");
        return false;
    }

    /// <summary>
    /// Elo de ESTEIRA: o trabalho do board passa a mover a fase do projeto. Sem ele, a chefe
    /// entregava cards enquanto a esteira do playbook ficava parada na fase 1 para sempre —
    /// nenhum objetivo saía de `pending` e nenhum documento exigido pela fase era produzido por
    /// ninguém. Uma falha aqui é logada e nunca impede o resto do ciclo.
    /// </summary>
    private async Task DrivePhaseAsync(
        string tenantId,
        string actorProfileId,
        ProjectRecord project,
        IServiceScope scope,
        CancellationToken token)
    {
        var driver = scope.ServiceProvider.GetService<Workflows.WorkflowPhaseDriver>();
        if (driver is null)
        {
            return;
        }

        var result = await driver.DriveAsync(tenantId, project, actorProfileId, token);
        if (result.CardsCreated > 0 || result.ObjectivesAdvanced > 0)
        {
            LogPhaseDriven(logger, project.Id, result.CardsCreated, result.ObjectivesAdvanced);
        }

        foreach (var failure in result.Failures)
        {
            LogPhaseDriveFailure(logger, project.Id, failure);
        }

        if (result.GateAwaitingHuman is { Length: > 0 } phase &&
            _announcedGates.Add($"{project.Id}:{phase}"))
        {
            LogPhaseGateReady(logger, project.Id, phase);
        }
    }

    /// <summary>
    /// Elo de ESCALAÇÃO: um card que estourou o teto de ciclos de review vira `escalated` — a
    /// cadeia recusa novas correções automáticas de propósito, porque repetir a mesma tentativa
    /// nunca resolveria. Até aqui nada no produto lia esse estado: o card simplesmente MORRIA em
    /// silêncio, e o dono só descobriria olhando o board.
    ///
    /// O chefe é a única voz com o usuário e responde pelo sucesso do projeto, então ele ANUNCIA:
    /// posta na conversa principal do projeto o que travou, o veredito do crítico e o que precisa
    /// ser decidido. Uma vez por card — o anúncio é idempotente por conteúdo, não vira spam a
    /// cada ciclo do loop.
    /// </summary>
    private async Task<int> AnnounceEscalatedCardsAsync(
        string tenantId,
        ProjectRecord project,
        IWorkBoardStore board,
        IServiceScope scope,
        CancellationToken token)
    {
        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, null, null, null, "active", null, 0, 100),
            token);
        var escalated = page.Items
            .Where(task => string.Equals(task.InternalState, "escalated", StringComparison.Ordinal))
            .Where(task => _announcedEscalations.Add(task.Id))
            .ToArray();
        if (escalated.Length == 0)
        {
            return 0;
        }

        // REPLANEJAMENTO antes de incomodar o dono. A máquina de estados prevê
        // `Escalated → Ready: Replanned` e essa transição nunca teve chamador em produção: o card
        // escalava e parava. Mas escalar significa que a MESMA abordagem falhou N vezes — repetir
        // não resolve, e chamar o humano de imediato joga para ele um trabalho que o chefe ainda
        // pode tentar de outro jeito.
        //
        // Uma tentativa, e só uma: a instrução revisada declara explicitamente que a abordagem
        // anterior esgotou os ciclos e que o card deve ser reduzido ao menor incremento
        // verificável. Se escalar de novo, aí sim é decisão humana — é o que a contagem de
        // replanejamentos abaixo garante, lendo o histórico durável de instruções.
        var stillEscalated = new List<BoardTaskRecord>();
        foreach (var task in escalated)
        {
            if (await TryReplanEscalatedAsync(
                tenantId, project, task, board,
                scope.ServiceProvider.GetRequiredService<IWorkChainStore>(), token))
            {
                _ = _announcedEscalations.Remove(task.Id);
                continue;
            }

            stillEscalated.Add(task);
        }

        escalated = [.. stillEscalated];
        if (escalated.Length == 0)
        {
            return 0;
        }

        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var open = await conversations.ListConversationsAsync(tenantId, project.Id, null, 1, token);

        // A memória do processo não basta como idempotência: cada restart do Host reapresentaria
        // a MESMA escalação, e o dono receberia o mesmo aviso repetido sem nenhum fato novo. O
        // registro durável do que já foi dito é a própria conversa — a mensagem carrega o id do
        // card, e é por ele que reconhecemos o aviso já publicado.
        var alreadyAnnounced = new HashSet<string>(StringComparer.Ordinal);
        if (open.Count > 0)
        {
            var history = await conversations.ListMessagesAsync(tenantId, open[0].Id, null, 200, token);
            foreach (var previous in history.Where(entry =>
                entry.Content.Contains(EscalationMarker, StringComparison.Ordinal)))
            {
                foreach (var task in escalated)
                {
                    if (previous.Content.Contains(task.Id, StringComparison.Ordinal))
                    {
                        _ = alreadyAnnounced.Add(task.Id);
                    }
                }
            }
        }

        escalated = [.. escalated.Where(task => !alreadyAnnounced.Contains(task.Id))];
        if (escalated.Length == 0)
        {
            return 0;
        }
        if (open.Count == 0)
        {
            // Sem conversa não há a quem anunciar; o log permanece como registro e o card volta
            // a ser anunciado quando existir canal (o Add acima já o marcou, então liberamos).
            foreach (var task in escalated)
            {
                _ = _announcedEscalations.Remove(task.Id);
                LogCardEscalated(logger, task.Id, "sem conversa no projeto");
            }

            return 0;
        }

        var now = clock.UtcNow;
        var announced = 0;
        foreach (var task in escalated)
        {
            var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
            var rejected = attempts.Count(attempt =>
                string.Equals(attempt.State, "failed", StringComparison.Ordinal));
            var findings = "(o review não registrou achados estruturados)";
            if (attempts.Count > 0)
            {
                var events = await board.ListAttemptEventsAsync(tenantId, attempts[^1].Id, null, 50, token);
                findings = events.LastOrDefault(entry =>
                    string.Equals(entry.Kind, "note", StringComparison.Ordinal))?.Content ?? findings;
            }

            var content =
                $"{EscalationMarker} **{task.Title}** (card {task.Id}).\n\n" +
                $"Ele foi reprovado {rejected} vez(es) pela revisão independente e atingiu o limite de " +
                "ciclos de correção. Continuar tentando do mesmo jeito só repetiria o mesmo resultado, " +
                "então parei e trouxe para você.\n\n" +
                $"O que a revisão apontou:\n{findings}\n\n" +
                "Me diga como prefere seguir: mudar o critério de aceite, reduzir o escopo do card, " +
                "ou tratar isso como decisão de projeto.";

            var result = await conversations.CreateMessageAsync(
                new MessageCreateCommand(
                    tenantId,
                    new MessageRecord(
                        tenantId, project.Id, UlidValue.New(now).ToString(), open[0].Id,
                        "chief", null, project.ChiefAgentId, content, null, now),
                    now),
                token);
            if (result.Status == MessageMutationStatus.Applied)
            {
                announced++;
                LogCardEscalated(logger, task.Id, $"anunciado ao dono após {rejected} reprovação(ões)");
            }
        }

        return announced;
    }

    /// <summary>
    /// Resolve as solicitações estruturadas que os agentes deixaram para a chefe. É o elo que
    /// substitui o caminho caro: até aqui, um executor que precisava de uma informação só
    /// conseguia falhar, ser reprovado e escalar depois de N ciclos.
    /// </summary>
    private static async Task<int> ResolveAgentRequestsAsync(
        string tenantId,
        ProjectRecord project,
        IServiceScope scope,
        CancellationToken token)
    {
        var resolver = scope.ServiceProvider.GetService<AgentRequestResolver>();
        if (resolver is null)
        {
            return 0;
        }

        var resolutions = await resolver.ResolveOpenAsync(tenantId, project.Id, token);
        return resolutions.Count;
    }

    /// <summary>
    /// INTEGRA os cards já aprovados pela revisão independente.
    ///
    /// Este elo faltava, e a sua ausência era o gargalo humano mais caro do produto: um card
    /// revisado e aprovado por agente DISTINTO ficava parado em `approved` até alguém clicar em
    /// "merge" na tela. Como é o merge que fecha o card e o card fechado é que libera a próxima
    /// onda do plano, a fábrica inteira dependia de o dono estar disponível — card a card, em
    /// todos os modos. Merge de trabalho já revisado não é decisão de stakeholder.
    ///
    /// O gate humano continua existindo onde significa alguma coisa: a TRANSIÇÃO DE FASE, conforme
    /// o modo do projeto. E a invariante que protege o código continua intacta — só chega aqui
    /// card em `approved`, que exige revisor diferente de quem produziu.
    /// </summary>
    private async Task<int> IntegrateApprovedCardsAsync(
        string tenantId,
        ProjectRecord project,
        IWorkBoardStore board,
        IServiceScope scope,
        CancellationToken token)
    {
        var integration = scope.ServiceProvider.GetService<WorkBoard.TaskIntegrationService>();
        if (integration is null)
        {
            return 0;
        }

        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, null, null, null, "active", null, 0, 50),
            token);
        var integrated = 0;
        foreach (var task in page.Items)
        {
            if (!string.Equals(task.InternalState, "approved", StringComparison.Ordinal))
            {
                continue;
            }

            var outcome = await integration.IntegrateAsync(
                tenantId, task.Id, ChiefIntegrationActor, token);
            if (outcome.Integrated)
            {
                integrated++;
                LogCardIntegrated(logger, task.Id, outcome.Branch ?? "-");
            }
            else
            {
                // Conflito de merge, repositório ausente ou cadeia recusando são fatos operáveis:
                // ficam no log com o código tipado e o card permanece em `approved` para a próxima
                // rodada — nada é dado como integrado sem ter sido.
                LogCardIntegrationRefused(logger, task.Id, outcome.ReasonCode);
            }
        }

        return integrated;
    }

    /// <summary>Ator registrado na cadeia quando quem integra é a chefe, não um humano.</summary>
    public const string ChiefIntegrationActor = "chief";

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: card {TaskId} integrado ({Branch}).")]
    private static partial void LogCardIntegrated(ILogger logger, string taskId, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: integração do card {TaskId} adiada: {ReasonCode}.")]
    private static partial void LogCardIntegrationRefused(ILogger logger, string taskId, string reasonCode);

    /// <summary>
    /// Leva ao dono os cards que NENHUMA conta pode executar por motivo estrutural. O scheduler já
    /// era fail-closed — ele recusa em vez de escalar para qualquer um —, mas a recusa morria no
    /// log: o card ficava adiado a cada ciclo, para sempre, e do lado de fora parecia backlog
    /// normal. Bloquear sem contar é meio caminho; o chefe é a única voz com o usuário e é ele
    /// quem responde pelo projeto parado.
    ///
    /// Só o ESTRUTURAL é anunciado (ver <see cref="StructuralRefusals"/>): cota, cooldown,
    /// concorrência e orçamento da rodada se resolvem sozinhos e virariam ruído. A idempotência é
    /// a mesma da escalação — o id do card viaja na mensagem e a conversa é o registro durável,
    /// então um restart não repete o aviso.
    /// </summary>
    private async Task<int> AnnounceUndispatchableCardsAsync(
        string tenantId,
        ProjectRecord project,
        IReadOnlyList<(ChiefCard Card, ChiefCardResolution Resolution, BoardTaskRecord Task, string InstructionVersionId)> cards,
        IReadOnlyList<ChiefDeferral> deferrals,
        IServiceScope scope,
        CancellationToken token)
    {
        var structural = deferrals
            .Where(ChiefDeferralTriage.IsStructural)
            .Where(deferral => _announcedUndispatchable.Add(deferral.Card.TaskId))
            .ToArray();
        if (structural.Length == 0)
        {
            return 0;
        }

        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var open = await conversations.ListConversationsAsync(tenantId, project.Id, null, 1, token);
        if (open.Count == 0)
        {
            foreach (var deferral in structural)
            {
                _ = _announcedUndispatchable.Remove(deferral.Card.TaskId);
            }

            return 0;
        }

        var history = await conversations.ListMessagesAsync(tenantId, open[0].Id, null, 200, token);
        var alreadyAnnounced = history
            .Where(entry => entry.Content.Contains(UndispatchableMarker, StringComparison.Ordinal))
            .ToArray();

        var now = clock.UtcNow;
        var announced = 0;
        foreach (var deferral in structural)
        {
            if (alreadyAnnounced.Any(entry =>
                    entry.Content.Contains(deferral.Card.TaskId, StringComparison.Ordinal)))
            {
                continue;
            }

            var title = cards
                .FirstOrDefault(entry => string.Equals(
                    entry.Card.TaskId, deferral.Card.TaskId, StringComparison.Ordinal))
                .Task?.Title ?? deferral.Card.TaskId;
            var refusals = string.Join(
                "\n",
                deferral.Candidates!.Select(candidate => $"- {candidate.Alias}: `{candidate.ReasonCode}`"));
            var content =
                $"{UndispatchableMarker} **{title}** (card {deferral.Card.TaskId}).\n\n" +
                $"Ele precisa do papel `{deferral.Card.Role}` com a capacidade `{deferral.Card.RequiredCapability}`, " +
                "e nenhuma conta da frota atende — por motivo que esperar não resolve. " +
                "Não vou atribuir para qualquer agente disponível, então o card fica parado até isto mudar.\n\n" +
                $"O que cada conta respondeu:\n{refusals}\n\n" +
                "Para destravar: habilitar/autenticar uma conta com esse papel, ou me dizer para " +
                "reformular o card em algo que a frota atual consiga executar.";

            var result = await conversations.CreateMessageAsync(
                new MessageCreateCommand(
                    tenantId,
                    new MessageRecord(
                        tenantId, project.Id, UlidValue.New(now).ToString(), open[0].Id,
                        "chief", null, project.ChiefAgentId, content, null, now),
                    now),
                token);
            if (result.Status == MessageMutationStatus.Applied)
            {
                announced++;
                LogCardUndispatchable(logger, deferral.Card.TaskId, deferral.Card.Role);
            }
        }

        return announced;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} sem executor possível para o papel '{Role}' — anunciado ao dono.")]
    private static partial void LogCardUndispatchable(ILogger logger, string taskId, string role);

    /// <summary>Marca que identifica, no histórico durável, uma instrução de replanejamento.</summary>
    private const string ReplanMarker = "## Replanejamento após escalonamento";

    /// <summary>
    /// Tenta UMA estratégia revisada para um card escalado. Devolve <c>true</c> quando replanejou
    /// (o card volta a `ready`); <c>false</c> quando o replanejamento já foi gasto e o caso é
    /// mesmo do humano.
    ///
    /// O limite de uma tentativa não é arbitrário: escalonamento já significa N ciclos de review
    /// reprovados, e um replanejamento automático sem teto reproduziria exatamente o laço infinito
    /// que a escalação existe para cortar. O histórico de instruções é o registro durável desse
    /// gasto — sobrevive a restart sem estado em memória.
    /// </summary>
    private async Task<bool> TryReplanEscalatedAsync(
        string tenantId,
        ProjectRecord project,
        BoardTaskRecord task,
        IWorkBoardStore board,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var instructions = await board.ListInstructionsAsync(tenantId, task.Id, null, 100, token);
        if (instructions.Count == 0 ||
            instructions.Any(instruction =>
                instruction.Body.Contains(ReplanMarker, StringComparison.Ordinal)))
        {
            return false;
        }

        var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
        var rejected = attempts.Count(attempt =>
            string.Equals(attempt.State, "failed", StringComparison.Ordinal));
        var content =
            $"{instructions[^1].Body}\n\n{ReplanMarker}\n" +
            $"A abordagem anterior esgotou os ciclos de revisão ({rejected} reprovação(ões)) e NÃO " +
            "deve ser repetida como está. Antes de escrever qualquer código:\n" +
            "1. Releia os achados da revisão e diga, em uma linha, por que a abordagem anterior " +
            "não fechou.\n" +
            "2. Reduza o card ao MENOR incremento verificável que satisfaça os critérios de aceite " +
            "— entregar menos, com evidência, vale mais que entregar tudo sem evidência.\n" +
            "3. Produza a evidência que faltou (execução de teste, log, diff) junto com a mudança.\n" +
            "Se após isto o escopo ainda não couber, registre o bloqueio em vez de tentar de novo.";

        var now = clock.UtcNow;
        var receipt = await chain.ReplanEscalatedTaskAsync(
            new WorkTaskReplanCommand(
                tenantId,
                task.BackingSolicitationId,
                task.Id,
                UlidValue.New(now).ToString(),
                content,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content))),
                project.ChiefAgentId,
                "chief.replan_after_escalation",
                $"attempts:{rejected}",
                task.Version,
                $"chief-loop-replan:{task.Id}",
                now),
            token);
        if (receipt.Status is not (WorkChainMutationStatus.Applied
            or WorkChainMutationStatus.IdempotentReplay))
        {
            LogCardEscalated(logger, task.Id, $"replanejamento recusado: {receipt.Status}");
            return false;
        }

        LogCardEscalated(logger, task.Id, $"replanejado com estratégia revisada após {rejected} reprovação(ões)");
        return true;
    }

    /// <summary>
    /// Elo de CORREÇÕES: um card reprovado pelo review (board `corrections`, cadeia `running`
    /// com tentativa `rejected`) recebe uma NOVA instrução imutável — a original mais os achados
    /// do crítico — e a própria mutação devolve o card a `ready`, de onde o despacho o reatribui
    /// (a cadeia recusa nova tentativa com a MESMA instrução reprovada). Idempotente por
    /// tentativa reprovada: uma correção por rejeição.
    /// </summary>
    internal async Task<int> PrepareCorrectionsAsync(
        string tenantId,
        ProjectRecord project,
        IWorkBoardStore board,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var prepared = 0;
        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, "corrections", null, null, "active", null, 0, 50),
            token);
        foreach (var task in page.Items)
        {
            if (!string.Equals(task.InternalState, "running", StringComparison.Ordinal))
            {
                continue;
            }

            var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
            var rejected = attempts.LastOrDefault(attempt =>
                string.Equals(attempt.State, "failed", StringComparison.Ordinal));
            if (rejected is null)
            {
                continue;
            }

            var instructions = await board.ListInstructionsAsync(tenantId, task.Id, null, 50, token);
            if (instructions.Count == 0)
            {
                continue;
            }

            // Os achados do crítico ficam no evento 'note' da tentativa reprovada.
            var events = await board.ListAttemptEventsAsync(tenantId, rejected.Id, null, 50, token);
            var findings = events.LastOrDefault(entry =>
                string.Equals(entry.Kind, "note", StringComparison.Ordinal))?.Content
                ?? "(o review não registrou achados estruturados)";

            var content =
                $"{instructions[^1].Body}\n\n## Correções exigidas pelo review independente (tentativa {rejected.Id})\n{findings}\n" +
                "Feche TODOS os achados acima sem reintroduzir nenhum deles.";
            var now = clock.UtcNow;
            var receipt = await chain.AddInstructionVersionAsync(
                new WorkInstructionVersionCreateCommand(
                    tenantId, task.BackingSolicitationId, task.Id,
                    UlidValue.New(now).ToString(), content,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(content))),
                    task.Version, $"chief-loop-correct:{rejected.Id}", now),
                token);
            if (receipt.Status is WorkChainMutationStatus.Applied
                or WorkChainMutationStatus.IdempotentReplay)
            {
                prepared++;
                LogCorrectionPrepared(logger, task.Id, rejected.Id);
            }
        }

        return prepared;
    }

    /// <summary>
    /// Elo de TRIAGEM por ondas: cards de um plano materializado sobem de `backlog` para `ready`
    /// quando a DoR passa (tipo despachável + instrução + não bloqueado) e TODAS as dependências
    /// declaradas do plano (códigos Tnn) estão entregues (`done`). Onda 1 (sem dependências) sobe
    /// imediatamente; as demais sobem à medida que o gate humano de merge conclui os provedores.
    /// Cards que exigem humano (spike/human_gate/decision) nunca sobem sozinhos.
    /// </summary>
    internal async Task<int> PromotePlannedCardsAsync(
        string tenantId,
        ProjectRecord project,
        IWorkBoardStore board,
        IDemandPlanStore plans,
        CancellationToken token)
    {
        var promoted = 0;
        var backlog = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, "backlog", null, null, "active", null, 0, 100),
            token);
        foreach (var group in backlog.Items
            .Where(task => task.DemandId is not null)
            .GroupBy(task => task.DemandId!, StringComparer.Ordinal))
        {
            var plan = await plans.GetByDemandAsync(tenantId, group.Key, token);
            if (plan is null || plan.MaterializedAt is null)
            {
                // Sem plano materializado, a triagem é humana — o chefe não promove.
                continue;
            }

            var dependenciesByCode = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in plan.Cards)
            {
                dependenciesByCode[DemandDecompositionPlanner.CodeOf(card.ProposedTitle)] = card.Dependencies;
            }

            // Estado ATUAL de todos os cards da demanda (em qualquer coluna) por código estável.
            var demandTasks = await board.PageTasksAsync(
                tenantId,
                new BoardTaskPageQuery(project.Id, group.Key, null, null, null, null, "active", null, 0, 100),
                token);
            var stateByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sibling in demandTasks.Items)
            {
                stateByCode[DemandDecompositionPlanner.CodeOf(sibling.Title)] = sibling.State;
            }

            foreach (var task in group)
            {
                // A triagem por ondas promove backlog→ready quando as dependências foram
                // ENTREGUES. Ela NÃO decide quem executa: cards que exigem humano (spike,
                // human_gate, decision) também precisam chegar a `ready`, senão o gate fica
                // invisível para o dono, preso no backlog para sempre — e o gate humano é
                // justamente o ponto em que o produto para e pede decisão. Quem impede a
                // EXECUÇÃO automática desses tipos é o gate de despacho, não esta promoção.
                var blocked = string.Equals(task.State, "blocked", StringComparison.Ordinal) ||
                    !string.IsNullOrWhiteSpace(task.BlockedReason);
                if (blocked || task.InstructionVersion < 1)
                {
                    continue;
                }

                var code = DemandDecompositionPlanner.CodeOf(task.Title);
                if (!dependenciesByCode.TryGetValue(code, out var dependencies))
                {
                    continue;
                }

                var satisfied = dependencies.All(dependency =>
                    stateByCode.TryGetValue(dependency, out var state) &&
                    string.Equals(state, "done", StringComparison.Ordinal));
                if (!satisfied)
                {
                    continue;
                }

                await board.MoveTaskAsync(
                    new BoardTaskMoveCommand(
                        tenantId, task.Id, "ready",
                        "triagem automática do chefe: DoR ok e dependências do plano entregues",
                        "agent", clock.UtcNow),
                    token);
                promoted++;
                LogCardPromoted(logger, task.Id, code);
            }
        }

        return promoted;
    }

    /// <summary>
    /// Escolhe a conta do crítico: papel `critic`, HABILITADA, adapter real, alias DIFERENTE do
    /// ator, fora de cooldown/circuito — por prioridade e desempate determinístico por alias.
    /// </summary>
    private string? SelectCriticAlias(string producerAlias, DateTimeOffset now)
    {
        var signals = CapacitySignals(now);
        return accounts.List()
            .Where(account =>
                account.State != AgentAccountState.Disabled &&
                account.AllowedRoles.Contains("critic", StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(account.Alias, producerAlias, StringComparison.OrdinalIgnoreCase) &&
                ExternalAgentExecutorFactory.IsImplemented(account.ExecutorId))
            .Where(account =>
            {
                var record = availability.Get(account.Alias);
                var coolingDown = record?.CooldownUntil is { } until && until > now &&
                    record.State is AgentAccountState.QuotaLimited or AgentAccountState.CoolingDown;
                return !coolingDown && !signals.ContainsKey(account.Alias);
            })
            .OrderByDescending(account => account.Priority)
            .ThenBy(account => account.Alias, StringComparer.Ordinal)
            .Select(account => account.Alias)
            .FirstOrDefault();
    }

    /// <summary>
    /// Colheita git da tentativa: se a worktree sobreviveu (worker terminou sem commitar), commita
    /// os restos na branch da tentativa e remove a worktree. Nunca destrói trabalho; falha aqui é
    /// logada e não impede a colheita da cadeia (o diff apenas refletirá o que está na branch).
    /// </summary>
    private async Task TryHarvestWorktreeAsync(
        ProjectRecord project,
        string controlledRoot,
        string attemptId,
        string branchName,
        CancellationToken token)
    {
        var worktreePath = System.IO.Path.Combine(controlledRoot, "worktrees", attemptId);
        if (!System.IO.Directory.Exists(worktreePath))
        {
            return;
        }

        try
        {
            var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl!);
            using var manager = await GitWorktreeManager.OpenAsync(repositoryRoot, controlledRoot, token);
            _ = await manager.CommitWorktreeLeftoversAsync(
                worktreePath, $"chore(harness): colheita da tentativa {attemptId}", token);
            _ = await manager.RemoveTaskWorktreeAsync(branchName, worktreePath, deleteBranch: false, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogWorktreeHarvestFailure(logger, attemptId, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: run da tentativa {AttemptId} colhido — card {TaskId} aguarda review.")]
    private static partial void LogRunHarvested(ILogger logger, string taskId, string attemptId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: run {Status} da tentativa {AttemptId} — card {TaskId} devolvido a `ready`.")]
    private static partial void LogRunRequeued(ILogger logger, string taskId, string attemptId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: review da tentativa {AttemptId} (card {TaskId}) por {CriticAlias}: {Decision}.")]
    private static partial void LogReviewApplied(ILogger logger, string taskId, string attemptId, string criticAlias, string decision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: review da tentativa {AttemptId} (card {TaskId}) adiado por falha de infraestrutura: {ReasonCode}.")]
    private static partial void LogReviewInfrastructureFailure(ILogger logger, string taskId, string attemptId, string reasonCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: nenhum crítico disponível ≠ ator {ProducerAlias} para o card {TaskId}; review adiado.")]
    private static partial void LogNoCriticAvailable(ILogger logger, string taskId, string producerAlias);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: card {TaskId} ({Code}) promovido a `ready` pela triagem por ondas.")]
    private static partial void LogCardPromoted(ILogger logger, string taskId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: colheita da worktree da tentativa {AttemptId} falhou: {ErrorType}.")]
    private static partial void LogWorktreeHarvestFailure(ILogger logger, string attemptId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: acompanhamento do projeto {ProjectId} falhou neste ciclo: {ErrorType}.")]
    private static partial void LogFollowUpFailure(ILogger logger, string projectId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} adiado: {ReasonCode} (volta: {RetryAfter}; contas: {Candidates}).")]
    private static partial void LogCardDeferred(ILogger logger, string taskId, string reasonCode, string retryAfter, string candidates);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: esteira do projeto {ProjectId} — {Created} card(s) de artefato criado(s), {Advanced} objetivo(s) de fase concluído(s).")]
    private static partial void LogPhaseDriven(ILogger logger, string projectId, int created, int advanced);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: esteira do projeto {ProjectId} — avanço de objetivo RECUSADO: {Failure}.")]
    private static partial void LogPhaseDriveFailure(ILogger logger, string projectId, string failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: projeto {ProjectId} — fase '{Phase}' com todos os artefatos entregues; o portão aguarda decisão humana (Default-FAIL, HITL).")]
    private static partial void LogPhaseGateReady(ILogger logger, string projectId, string phase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} ESCALADO — {Detail}.")]
    private static partial void LogCardEscalated(ILogger logger, string taskId, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: card {TaskId} recebeu instrução corretiva após a reprovação da tentativa {AttemptId} e voltou a `ready`.")]
    private static partial void LogCorrectionPrepared(ILogger logger, string taskId, string attemptId);

    private async Task<bool> LaunchAsync(
        string tenantId,
        ProjectRecord project,
        ChiefCardResolution resolution,
        string accountAlias,
        string? model,
        BoardTaskRecord task,
        string instructionVersionId,
        IReadOnlyList<AgentDefinitionRecord> personas,
        string controlledRoot,
        bool chiefReinforcement,
        IWorkBoardStore board,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var now = clock.UtcNow;

        // Inicia uma tentativa durável na cadeia de trabalho (id nunca solto).
        var attemptId = UlidValue.New(now).ToString();
        var assigned = await chain.AssignTaskAsync(
            new WorkTaskAssignmentCommand(
                tenantId,
                task.BackingSolicitationId,
                task.Id,
                instructionVersionId,
                accountAlias,
                "chief",
                "bruna",
                $"attempt:{attemptId}",
                task.Version,
                $"chief-loop-assignment:{attemptId}",
                now),
            token);
        if (assigned.Status != WorkChainMutationStatus.Applied &&
            (assigned.Status != WorkChainMutationStatus.IdempotentReplay ||
                assigned.TaskState != "assigned"))
        {
            LogAttemptNotStarted(logger, task.Id, assigned.Status.ToString());
            return false;
        }

        var started = await chain.StartAttemptAsync(
            new WorkAttemptStartCommand(
                tenantId, task.BackingSolicitationId, task.Id, instructionVersionId, attemptId,
                accountAlias, assigned.TaskVersion!.Value, $"chief-loop-heartbeat:{attemptId}", now),
            token);
        if (started.Status is not (WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay))
        {
            LogAttemptNotStarted(logger, task.Id, started.Status.ToString());
            return false;
        }

        // Briefing = PERSONA (do catálogo) + CARD (a demanda). A persona pedida pelo card pode ter
        // sido DECLARADA pelo Chefe; o texto dele propõe, o catálogo decide. Uma chave inexistente,
        // desabilitada ou que não seja de especialista cai no fallback inferido — nunca em "sem
        // persona", que degradaria o briefing para o escopo cru.
        var persona = FindPersona(personas, resolution.PersonaKey);
        if (persona is not null && !IsEligible(persona, project.Id, task.Priority))
        {
            // Existe no catálogo, mas não pode assumir ESTE card. Cair no fallback é melhor do
            // que delegar a quem o sistema já sabe que não vai conseguir entregar.
            LogPersonaNotEligible(logger, task.Id, persona.Key);
            persona = null;
        }

        if (persona is null && !string.Equals(
                resolution.PersonaKey, resolution.InferredPersonaKey, StringComparison.OrdinalIgnoreCase))
        {
            LogPersonaNotInCatalog(logger, task.Id, resolution.PersonaKey);
            persona = FindPersona(personas, resolution.InferredPersonaKey);
        }

        var briefing = persona is null
            ? resolution.Card.Scope
            : PersonaCardComposer.Compose(ToContent(persona), resolution.Card);

        var pathScopeKind = string.Equals(
            resolution.Role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)
            ? AgentPathScopeKind.FrontendSpecialist
            : AgentPathScopeKind.Backend;

        var snapshot = await orchestrator.StartAsync(
            new StartAgentRunCommand
            {
                TenantId = tenantId,
                ProjectId = project.Id,
                TaskId = task.Id,
                AttemptId = attemptId,
                Role = resolution.Role,
                AccountAlias = accountAlias,
                Instruction = briefing,
                RepositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl!),
                ControlledRoot = controlledRoot,
                BranchName = $"task/agent-run-{attemptId.ToLowerInvariant()}",
                WorktreePath = System.IO.Path.Combine(controlledRoot, "worktrees", attemptId),
                ScopeClaims = resolution.ScopeClaims,
                Owner = "chief-backlog-loop",
                IdempotencyKey = $"chief-loop:{attemptId}",
                PathScopeKind = pathScopeKind,
                Access = ExternalAgentAccess.Workspace,
                Model = model,
                RiskTier = resolution.Card.RiskTier,
                AcceptanceCriteria = resolution.Card.AcceptanceCriteria,
                // null é deliberadamente fail-closed no orquestrador: uma persona resolvida que
                // declara zero ferramentas é diferente de não ter resolvido persona alguma.
                RequiredToolIds = persona?.ToolIds,
                ChiefReinforcement = chiefReinforcement,
            },
            token);

        if (snapshot.Status is AgentRunStatus.Rejected or AgentRunStatus.ScopeConflict)
        {
            LogRunRejected(logger, task.Id, snapshot.Status.ToString(), snapshot.FinalError ?? string.Empty);
            _dispatchBackoff[task.Id] = clock.UtcNow.Add(RejectedDispatchBackoff);

            // COMPENSAÇÃO: a tentativa durável já estava `running` quando o orquestrador recusou
            // o run (ex.: conflito de claim com um run vivo). Sem abandoná-la, o card ficaria em
            // `development` com uma tentativa órfã PARA SEMPRE. O abandono devolve o card a
            // `ready` e o próximo ciclo re-tenta quando o claim liberar.
            _ = await chain.ExpireAttemptLeaseAsync(
                new WorkAttemptLeaseExpiredCommand(
                    tenantId, task.BackingSolicitationId, task.Id, attemptId,
                    started.TaskVersion!.Value, $"chief-loop-compensate:{attemptId}", clock.UtcNow),
                token);
            return false;
        }

        // O card sai da fila `ready`: passa a `development` (o board_state válido que projeta o
        // estado interno `running` — ver migration 0013). Assim não é re-despachado no próximo ciclo.
        await board.MoveTaskAsync(
            new BoardTaskMoveCommand(tenantId, task.Id, "development", $"chief:{attemptId}", "agent", now),
            token);
        return true;
    }

    /// <summary>
    /// Só projeto ATIVO entra no ciclo autônomo. `paused` é decisão explícita do dono (endpoint
    /// `chief/pause`) e `archived` é fim de vida: nenhum dos dois pode consumir cota, slot de
    /// despacho ou disparar review.
    /// </summary>
    private static bool IsDispatchable(ProjectRecord project) =>
        string.Equals(project.State, "active", StringComparison.Ordinal);

    private static bool IsInsideControlledRoot(ProjectRecord project, string controlledRoot)
    {
        if (string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return false;
        }

        var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl);
        return System.IO.Directory.Exists(repositoryRoot) &&
            repositoryRoot.StartsWith(
                $"{controlledRoot}{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static int PriorityWeight(string priority) => priority.ToLowerInvariant() switch
    {
        "critical" => 100,
        "high" => 75,
        "medium" => 50,
        "low" => 25,
        _ => 50,
    };

    private static AgentDefinitionContent ToContent(AgentDefinitionRecord value) => new(
        value.Key, value.Name, value.Role, value.Specialty, value.Description, value.DefaultModelId,
        value.SkillIds, value.ToolIds, value.Persona, value.Mission, value.OperatingPrinciples ?? [],
        value.Deliverables ?? [], value.QualityCriteria ?? [], value.CommunicationStyle,
        value.Limitations ?? [], value.Stacks ?? [], value.DefaultEffort, value.PreferredAccountId,
        value.FallbackModelIds ?? [], value.Team, value.ActorCritic, value.Risk);
}
