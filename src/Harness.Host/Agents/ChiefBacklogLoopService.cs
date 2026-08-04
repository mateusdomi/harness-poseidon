using Harness.Host.Architecture;
using Harness.Host.Documents;
using Harness.Host.WorkBoard;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Modules.Providers.Application;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.CodeGraph;
using Harness.SharedKernel.Providers;
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
    CodeGraphDerivationService codeGraph,
    AgentAccountScheduler scheduler,
    IProviderCatalogStore providerCatalog,
    ILogger<ChiefBacklogLoopService> logger) : BackgroundService
{
    /// <summary>Despachante em escala (Fase 10) — puro e determinístico, um por processo.</summary>
    private static readonly ScaleDispatcher ScaleGate = new();

    private readonly AgentAccountScheduler _scheduler = scheduler;

    /// <summary>
    /// Quantos slots cada projeto ocupou neste processo, por tenant. A fila sempre começa pelos
    /// menos atendidos; isso impede que dois backlogs longos recapturem todos os slots a cada
    /// rodada. É estado apenas de escalonamento: um restart zera os contadores e usa atividade
    /// recente como desempate, sem alterar nenhuma verdade de negócio.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, int>> _projectDispatchCountsByTenant =
        new(StringComparer.Ordinal);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: etapa '{PhaseName}' do projeto {ProjectId} anunciada ao dono.")]
    private static partial void LogPhaseMilestoneAnnounced(
        ILogger logger, string projectId, string phaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Chief: card {TaskId} (tentativa {AttemptId}) ESCALADO — revisão independente indisponível após {Failures} adiamentos por {ReasonCode}.")]
    private static partial void LogReviewUnavailableEscalated(
        ILogger logger, string taskId, string attemptId, string reasonCode, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} pulado — o papel '{Role}' não possui escopo de escrita; despachá-lo seria rejeitado por agent_path_scope_empty a cada ciclo.")]
    private static partial void LogCardWithoutWriteScope(ILogger logger, string taskId, string role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} (card_type={CardType}) NÃO despachável — pulado por prontidão (DoR): {Blockers}")]
    private static partial void LogCardNotDispatchable(ILogger logger, string taskId, string cardType, string blockers);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Chief: projeto {ProjectId} está em modo MANUAL — o laço não despacha; o disparo é humano.")]
    private static partial void LogProjectManualMode(ILogger logger, string projectId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Chief: card {TaskId} sem progresso há {Runs} run(s) seguidos — nenhum token produzido. " +
                  "Freio de {DelaySeconds}s antes do próximo despacho; qualquer saída real zera a contagem.")]
    private static partial void LogNoProgressBrake(ILogger logger, string taskId, int runs, int delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: repositório gerenciado do projeto {ProjectId} não pôde ser preparado para execução ({ErrorType}); nenhum card será consumido neste ciclo.")]
    private static partial void LogManagedRepositoryUnavailable(
        ILogger logger, string projectId, string errorType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: modo de operação do projeto {ProjectId} indisponível — projeto fora DESTE ciclo; nenhum despacho é presumido.")]
    private static partial void LogOperationModeUnavailable(ILogger logger, string projectId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} ESGOTOU o orçamento de rodadas ({Spent}/{MaxRounds}, {ReasonCode}) — escalado em vez de redespachado.")]
    private static partial void LogBudgetExhausted(
        ILogger logger, string taskId, int spent, int maxRounds, string reasonCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} com CIRCUITO ABERTO ({Failures} falhas consecutivas) — não é redespachado; só o replanejamento da Bruna o reabre.")]
    private static partial void LogCardCircuitOpen(ILogger logger, string taskId, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: índice de código indisponível para o projeto {ProjectId} neste ciclo ({ErrorType}); impacto permanece não medido.")]
    private static partial void LogCodeGraphUnavailable(ILogger logger, string projectId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: despacho do projeto {ProjectId} bloqueado por {ErrorCount} erro(s) de compilação no índice.")]
    private static partial void LogCodeDiagnosticsBlocked(ILogger logger, string projectId, int errorCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: gates da entrega do card {TaskId} (tentativa {AttemptId}) executados pela plataforma: {ReasonCode} — {Detail}")]
    private static partial void LogDeliveryGatesRan(
        ILogger logger, string taskId, string attemptId, string reasonCode, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} não despachado por divergência {Code} com {RelatedCardId}: {Explanation} (evidências={EvidenceCount}).")]
    private static partial void LogPlanGraphBlocked(
        ILogger logger,
        string taskId,
        string code,
        string relatedCardId,
        string explanation,
        int evidenceCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chief: aviso de esteira publicado no projeto {ProjectId} — parada={Stalled}, {IdleMinutes} min sem entrega.")]
    private static partial void LogDeliveryStallAnnounced(
        ILogger logger, string projectId, bool stalled, int idleMinutes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Chief: card {TaskId} PAROU de progredir — {Attempts} tentativas seguidas sem produzir um token. Parede: {Reason}. Não é falha do card; olhe a infraestrutura antes de replanejar.")]
    private static partial void LogCardNoProgress(
        ILogger logger, string taskId, int attempts, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Chief: card {TaskId} (papel {Role}) foi planejado e NÃO recebeu desfecho — nem despacho, nem adiamento. Isto é um defeito do planejador, não espera normal.")]
    private static partial void LogCardVanishedFromCycle(ILogger logger, string taskId, string role);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chief: card {TaskId} retido pelo teto global de execução ({LiveRuns} em voo, teto {Ceiling}); volta na próxima rodada.")]
    private static partial void LogCardHeldByGlobalCeiling(
        ILogger logger, string taskId, int liveRuns, int ceiling);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: impacto do card {TaskId}: risco {DeclaredRisk}->{EffectiveRisk}, revisão {ReviewDepth}, medido={Measured}.")]
    private static partial void LogBlastRadiusAssessed(
        ILogger logger,
        string taskId,
        RiskTier declaredRisk,
        RiskTier effectiveRisk,
        int reviewDepth,
        bool measured);

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
        // Fase 1E: `AutoDispatchEnabled` continua sendo o INTERRUPTOR DO OPERADOR — um kill switch
        // que desliga a fábrica inteira quando alguém precisa parar tudo. Ele não é mais a decisão
        // sobre autonomia: essa é do PROJETO, e é avaliada por projeto dentro do ciclo. Um
        // interruptor global respondendo "o projeto A é autônomo?" só sabia dizer sim ou não para
        // todos, o que obrigava o dono a escolher entre automatizar tudo ou não automatizar nada.
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
        var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowCatalogStore>();
        // A raiz gerenciada: todo projeto criado pelo próprio Poseidon nasce sob ela.
        var repositories = scope.ServiceProvider.GetRequiredService<Harness.Host.Projects.ProjectRepositoryStorage>();
        var circuits = new CardCircuitBreakerService(
            scope.ServiceProvider.GetRequiredService<ICardCircuitBreakerStore>(),
            settings.CardCircuitFailureThreshold);

        var profileList = await profiles.ListAsync(token);
        if (profileList.Count == 0)
        {
            return (0, 0);
        }

        // Fase 1E: TODOS os tenants, não o primeiro. `profileList[0]` significava que, com dois
        // perfis na mesma instalação, o segundo projeto simplesmente nunca era despachado — e nada
        // no produto dizia isso. O laço não "falhava": ele trabalhava para um dono só, em silêncio.
        //
        // O isolamento é POR ITERAÇÃO: cada tenant lê seu catálogo, seus projetos e seus cards.
        // Uma falha em um tenant não pode impedir os demais de progredir, senão um projeto quebrado
        // paralisa a instalação inteira.
        var dispatched = 0;
        var deferred = 0;
        foreach (var profile in profileList)
        {
            token.ThrowIfCancellationRequested();
            var controlledRoot = System.IO.Path.GetFullPath(settings.ControlledRoot!);
            var personas = await catalog.ListDefinitionsForTenantAsync(profile.TenantId, null, 100, false, token);
            var plans = scope.ServiceProvider.GetRequiredService<IDemandPlanStore>();

            var listedProjects = await projects.ListAsync(profile.TenantId, null, 50, token);
            if (!_projectDispatchCountsByTenant.TryGetValue(profile.TenantId, out var dispatchCounts))
            {
                dispatchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                _projectDispatchCountsByTenant[profile.TenantId] = dispatchCounts;
            }
            var projectList = OrderProjectsForDispatch(listedProjects, dispatchCounts);
            foreach (var project in projectList)
            {
                token.ThrowIfCancellationRequested();

                // RECONCILIAÇÃO — roda ANTES dos portões de produção, de propósito.
                //
                // Pausar um projeto deve parar de PRODUZIR trabalho; não pode congelar o ledger. Uma
                // tentativa cujo processo morreu continua marcada `running` no banco, e o índice
                // `ux_work_attempts_one_active` impede qualquer nova tentativa naquele card. Como o
                // workspace órfão já foi liberado pela recuperação (e some das listas, que filtram
                // `released_at IS NULL`), o estado é IRREVERSÍVEL: nem despausar o projeto destrava.
                // Foi o que prendeu tentativas por seis dias em projetos pausados e manuais.
                //
                // Isto não despacha, não revisa, não integra e não anuncia — apenas fecha o que já
                // morreu. Nenhuma cota é gasta e nenhum slot de despacho é ocupado.
                //
                // O modo do projeto é resolvido AQUI, antes dos portões, só para responder a uma
                // pergunta: a colheita rica vai rodar neste ciclo? Se não vai — projeto pausado,
                // fora da raiz controlada ou manual — a reconciliação também precisa REGISTRAR o
                // run que terminou. Pausar deve impedir trabalho novo, não apagar trabalho já
                // feito: um run que concluiu e nunca foi colhido deixa o card preso para sempre.
                var willHarvest = IsDispatchable(project)
                    && IsInsideControlledRoot(project, controlledRoot, repositories.RootPath)
                    && await ResolvesToAutonomousAsync(workflows, profile.TenantId, project, token);

                try
                {
                    await ReconcileDeadAttemptsAsync(
                        profile.TenantId, project, board, chain, willHarvest, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFollowUpFailure(logger, exception, project.Id, exception.GetType().Name);
                }

                token.ThrowIfCancellationRequested();

                // `pause` do chefe precisa PARAR de verdade. Sem este filtro o loop continuava
                // colhendo, revisando e despachando cards de projeto pausado/arquivado — o botão
                // existia na API e não segurava nada, e um projeto que o dono mandou parar seguia
                // gastando cota e slot de despacho dos projetos ativos.
                if (!IsDispatchable(project))
                {
                    continue;
                }

                if (!IsInsideControlledRoot(project, controlledRoot, repositories.RootPath))
                {
                    continue;
                }

                // Migração operacional dos repositórios criados por versões anteriores: eles
                // tinham `.git`, mas nenhum commit/HEAD. O bootstrap idempotente cria somente a
                // revisão-base vazia necessária para uma worktree. Fazê-lo antes de iniciar a
                // tentativa evita consumir orçamento com uma falha que já conhecemos.
                if (IsUnder(System.IO.Path.GetFullPath(project.RepositoryUrl!), repositories.RootPath))
                {
                    try
                    {
                        _ = await repositories.EnsureInitializedAsync(
                            profile.TenantId, project.Key, token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        LogManagedRepositoryUnavailable(
                            logger, project.Id, exception.GetType().Name, exception);
                        continue;
                    }
                }

                // O gerenciador de worktrees exige que repositório E worktree pertençam à mesma
                // raiz controlada. Projetos trazidos pelo dono usam `ControlledRoot`; projetos
                // criados pelo Poseidon vivem na raiz gerenciada. Aceitar a segunda no filtro e
                // continuar passando a primeira ao executor fazia toda tentativa de projeto novo
                // falhar com `ArgumentException` antes de criar a worktree.
                var projectControlledRoot = repositories.ResolveControlledRoot(
                    project.RepositoryUrl!, controlledRoot);

                // Fase 1E: a AUTONOMIA é decisão do PROJETO, não um interruptor único da
                // instalação. Um projeto `manual` exige disparo humano e não pode ter card
                // despachado por um laço de fundo; um projeto `autonomous` avança sozinho. Antes,
                // a única escolha era automatizar tudo ou nada — e quem tem um projeto sensível ao
                // lado de um projeto rotineiro acabava desligando os dois.
                //
                // O AutonomousActionGuard segue INTOCADO: ele decide o que a autonomia pode fazer
                // depois que ela está permitida; isto decide apenas se ela está.
                //
                // O modo vem do RESOLVEDOR, não do campo do projeto: o campo é escrito uma vez, na
                // criação, e nunca acompanha a troca de modo feita pelo dono na tela. Ler o campo
                // direto faria o laço despachar um projeto que o dono já tinha posto em manual.
                Harness.Modules.Workflows.Application.ProjectOperationMode mode;
                try
                {
                    mode = await Harness.Host.Workflows.ProjectOperationModeResolver.ResolveAsync(
                        workflows, profile.TenantId, project.Id, project.OperationMode, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Não sabemos o modo. Despachar seria decidir por autonomia sem base; o projeto
                    // fica de fora DESTE ciclo e o próximo tenta de novo.
                    LogOperationModeUnavailable(logger, project.Id, exception);
                    continue;
                }

                if (mode == Harness.Modules.Workflows.Application.ProjectOperationMode.Manual)
                {
                    LogProjectManualMode(logger, project.Id);
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
                    await HarvestCompletedRunsAsync(
                        profile.TenantId, project, projectControlledRoot, board, chain,
                        scope.ServiceProvider.GetRequiredService<IModelInvocationStore>(), token);
                    token.ThrowIfCancellationRequested();
                    await ReviewAwaitingAttemptsAsync(
                        profile.TenantId,
                        project,
                        projectControlledRoot,
                        board,
                        chain,
                        scope.ServiceProvider.GetRequiredService<IModelInvocationStore>(),
                        scope.ServiceProvider.GetRequiredService<IWorkflowDocumentTemplateStore>(),
                        token);
                    token.ThrowIfCancellationRequested();
                    await PrepareCorrectionsAsync(profile.TenantId, project, board, chain, token);
                    token.ThrowIfCancellationRequested();
                    await ResolveAgentRequestsAsync(profile.TenantId, project, scope, token);
                    token.ThrowIfCancellationRequested();
                    await IntegrateApprovedCardsAsync(
                        profile.TenantId, profile.Id, project, projectControlledRoot, board, scope, token);
                    token.ThrowIfCancellationRequested();
                    await AnnounceEscalatedCardsAsync(
                        profile.TenantId, project, projectControlledRoot, board, scope, token);
                    token.ThrowIfCancellationRequested();
                    // Caminho BOM também é notícia: sem isto o dono só ouvia a Bruna quando algo
                    // travava, e um projeto saudável avançava fases inteiras em silêncio.
                    await AnnouncePhaseMilestonesAsync(profile.TenantId, project, scope, token);
                    await AnnounceDeliveryStallAsync(profile.TenantId, project, board, scope, token);
                    token.ThrowIfCancellationRequested();
                    await DrivePhaseAsync(profile.TenantId, profile.Id, project, scope, token);
                    token.ThrowIfCancellationRequested();
                    await PromotePlannedCardsAsync(profile.TenantId, project, board, plans, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFollowUpFailure(logger, exception, project.Id, exception.GetType().Name);
                }

                token.ThrowIfCancellationRequested();

                // Cards prontos para delegar: board_state `ready` (minúsculo — o enum é case-sensitive
                // no SQLite), não arquivados. Cards nascem em `backlog`; a triagem (humano/DoR) promove
                // a `ready` antes de o loop os enxergar.
                var page = await board.PageTasksAsync(
                    profile.TenantId,
                    new BoardTaskPageQuery(project.Id, null, null, "ready", null, null, "active", null, 0, 50),
                    token);
                if (page.Items.Count == 0)
                {
                    continue;
                }

                // Mapa das superfícies REAIS do repositório do projeto, lido uma vez por ciclo. É ele
                // que permite ao card reivindicar o módulo que ele mexe em vez de `src/**` inteiro —
                // sem isso, dois cards independentes do mesmo projeto nunca rodam juntos.
                var surfaceMap = string.IsNullOrWhiteSpace(project.RepositoryUrl)
                    ? RepositorySurfaceMap.Empty
                    : RepositorySurfaceMap.Build(System.IO.Path.GetFullPath(project.RepositoryUrl));
                var acceptedDocuments = await LoadAcceptedDocumentReferencesAsync(
                    profile.TenantId,
                    project.Id,
                    scope.ServiceProvider.GetRequiredService<IDocumentCatalogStore>(),
                    token);

                var cards = new List<(ChiefCard Card, ChiefCardResolution Resolution, BoardTaskRecord Task, string InstructionVersionId)>();
                foreach (var task in page.Items)
                {
                    token.ThrowIfCancellationRequested();
                    // Card recusado na largada há pouco (conflito de claim): esperar o escopo liberar é
                    // a decisão correta — re-tentar em seguida só produz tentativa fantasma.
                    if (_dispatchBackoff.TryGetValue(task.Id, out var retryAt) && retryAt > clock.UtcNow)
                    {
                        continue;
                    }

                    var instructions = await board.ListInstructionsAsync(profile.TenantId, task.Id, null, 50, token);

                    // Gate fail-safe da Definition of Ready: somente cards que representam trabalho
                    // delegável, com instrução e sem bloqueio, entram na fila. Contêineres e decisões
                    // humanas ('feature', 'human_gate', 'gate', 'decision') são pulados com bloqueador
                    // tipado; documentos, revisões e pesquisas são trabalho real e seguem para o
                    // profissional apropriado.
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
                        [.. attemptHistory.Select(attempt => new CardAttemptOutcome(
                            attempt.State,
                            attempt.FailureReason,
                            attempt.FinishedAt ?? attempt.StartedAt,
                            attempt.TokensOutput,
                            attempt.DurationMs))],
                        token);
                    // PARADA POR NÃO-PROGRESSO — pergunta diferente da culpa.
                    //
                    // A regra invertida do circuito diz, corretamente, que tentativa sem token
                    // nunca julgou o enunciado e não pune o card. O efeito colateral era que o
                    // sintoma mais grave — "não produziu absolutamente nada" — virava o único
                    // caso que nunca fazia nada parar: quatorze tentativas idênticas em uma hora
                    // e quarenta contra a mesma parede, em 03/08/2026. Aqui a esteira PARA de
                    // insistir sem acusar o card, e nomeia a parede para quem for olhar.
                    if (circuit.IsStalled(settings.CardNoProgressCeiling, clock.UtcNow))
                    {
                        LogCardNoProgress(
                            logger, task.Id, circuit.ConsecutiveNoProgress,
                            circuit.LastFailureReasonCode is { Length: > 0 } wall &&
                            !string.Equals(wall, "failed", StringComparison.Ordinal)
                                ? wall
                                : "sem motivo registrado pelo executor");
                        continue;
                    }

                    if (!circuit.IsDispatchable)
                    {
                        LogCardCircuitOpen(logger, task.Id, circuit.ConsecutiveFailures);
                        // O circuito aberto só reabre por replanejamento da Bruna — e ela não pode
                        // replanejar o que não sabe que parou. Sem escalar, o card ficava em
                        // `ready` para sempre, reexaminado a cada ciclo e invisível para todos.
                        _ = await chain.EscalateUndispatchableTaskAsync(
                            new WorkTaskUndispatchableCommand(
                                profile.TenantId, task.BackingSolicitationId, task.Id,
                                $"Tentamos executar este trabalho {circuit.ConsecutiveFailures} vezes " +
                                "seguidas e todas falharam do mesmo jeito. Insistir só repetiria o " +
                                "mesmo resultado, então parei: preciso rever o enunciado ou reduzir " +
                                "o que ele pede antes de tentar de novo.",
                                $"card:{task.Id}",
                                task.Version,
                                $"chief-loop-circuit-open:{task.Id}:{task.Version}",
                                clock.UtcNow),
                            token);
                        continue;
                    }

                    // Fase 1B: o ORÇAMENTO do card governa o despacho. A EffortPolicy existia e não
                    // decidia nada — o teto de rodadas era só o circuito por falha, que mede outra
                    // coisa (falha técnica, não esgotamento do plano). Um card que já consumiu as
                    // rodadas orçadas não é redespachado em silêncio: ele ESCALA, com o fato auditado.
                    var budget = await ReadCardBudgetAsync(profile.TenantId, task, plans, token);
                    var spentRounds = CountSpentRounds(attemptHistory);
                    if (budget is not null && spentRounds >= budget.MaxRounds)
                    {
                        // A MESMA pergunta do replanejamento, feita ao orçamento: uma rodada é
                        // gasta quando uma ABORDAGEM foi exercida. Sem isto o replanejamento
                        // aprovado virava um laço — ele devolvia o card a `ready` e o orçamento o
                        // reescalava no mesmo ciclo, gravando uma versão de instrução por volta e
                        // sem nunca despachar nada. Duas contagens da mesma coisa não podem
                        // discordar; a que ficasse com o proxy quebrado anularia a outra.
                        //
                        // O refinamento só roda NA FRONTEIRA, quando o corte cru já disse
                        // "esgotou": ler o diff de toda tentativa a cada tique custaria git por
                        // card por dez segundos, e a resposta só muda quando o card ia escalar.
                        spentRounds = await CountExercisedRoundsAsync(
                            project, projectControlledRoot, attemptHistory, token);
                    }

                    if (budget is not null && spentRounds >= budget.MaxRounds)
                    {
                        await EscalateBudgetExhaustionAsync(
                            profile.TenantId, project.Id, task, chain, budget, spentRounds, token);
                        continue;
                    }

                    var dispatchTask = task;
                    var dispatchInstruction = instructions[^1];
                    var refreshedInstruction = EnrichInstructionWithAcceptedDocuments(
                        dispatchInstruction.Body,
                        task.PhaseName,
                        acceptedDocuments);
                    if (!string.Equals(
                            refreshedInstruction,
                            dispatchInstruction.Body,
                            StringComparison.Ordinal))
                    {
                        try
                        {
                            var contextNow = clock.UtcNow;
                            dispatchInstruction = await board.AppendInstructionAsync(
                                new BoardInstructionAppendCommand(
                                    profile.TenantId,
                                    task.Id,
                                    UlidValue.New(contextNow).ToString(),
                                    refreshedInstruction,
                                    "chief",
                                    project.ChiefAgentId,
                                    contextNow),
                                token);
                            dispatchTask = await board.GetTaskAsync(
                                    profile.TenantId, task.Id, token)
                                ?? throw new InvalidOperationException(
                                    $"Card {task.Id} disappeared after its context was refreshed.");
                        }
                        catch (WorkBoardInvalidStateException)
                        {
                            // O card mudou entre a paginação e o refresh. Não despache com a versão
                            // obsoleta nem trate a corrida legítima como falha: o próximo ciclo relê.
                            continue;
                        }
                    }

                    var resolution = ChiefCardResolver.Resolve(
                        dispatchTask.Title,
                        dispatchInstruction.Body,
                        [],
                        dispatchTask.Priority,
                        surfaceMap: surfaceMap);

                    // F-17: a persona declarada carrega escopos que devem restringir o escopo do
                    // papel. Resolvemos primeiro para saber qual persona foi escolhida, depois
                    // recalculamos com os AllowedScopes/DeniedScopes dela como restrição adicional.
                    var dispatchPersona = FindPersona(personas, resolution.PersonaKey)
                        ?? FindPersona(personas, resolution.InferredPersonaKey);
                    if (dispatchPersona is { AllowedScopes.Count: > 0 } or { DeniedScopes.Count: > 0 })
                    {
                        resolution = ChiefCardResolver.Resolve(
                            dispatchTask.Title,
                            dispatchInstruction.Body,
                            [],
                            dispatchTask.Priority,
                            explicitPersonaKey: resolution.PersonaKey,
                            explicitRole: resolution.Role,
                            surfaceMap: surfaceMap,
                            personaAllowedScopes: dispatchPersona.AllowedScopes,
                            personaDeniedScopes: dispatchPersona.DeniedScopes);
                    }

                    // Um papel SEM escopo de escrita (o crítico, por exemplo) produz claim vazia, e a
                    // política de path rejeitaria a tentativa com `agent_path_scope_empty`. Pular o
                    // card evitava queimar slot — mas pular é a resposta certa uma vez e errada para
                    // sempre: o card ficava em `ready` indefinidamente, o loop o reexaminava a cada
                    // ciclo, o log crescia sem limite e ninguém ficava sabendo que aquele trabalho
                    // nunca ia acontecer. Agora ele vira impedimento escalado, que a Diretora de
                    // Engenharia anuncia — é uma decisão, não um silêncio.
                    if (resolution.ScopeClaims.Count == 0)
                    {
                        LogCardWithoutWriteScope(logger, task.Id, resolution.Role);
                        _ = await chain.EscalateUndispatchableTaskAsync(
                            new WorkTaskUndispatchableCommand(
                                profile.TenantId, dispatchTask.BackingSolicitationId, dispatchTask.Id,
                                $"Este trabalho foi organizado para uma especialidade que não pode " +
                                $"alterar arquivo nenhum ('{resolution.Role}'), então nunca sairia do " +
                                "lugar por mais que a equipe tentasse. Preciso redefinir quem faz e o " +
                                "que ele pode tocar.",
                                $"card:{dispatchTask.Id}",
                                dispatchTask.Version,
                                $"chief-loop-undispatchable:{dispatchTask.Id}:{dispatchTask.Version}",
                                clock.UtcNow),
                            token);
                        continue;
                    }

                    cards.Add((
                        new ChiefCard(task.Id, project.Id, resolution.Role, resolution.RequiredCapability,
                            PriorityWeight(task.Priority), resolution.ScopeClaims,
                            // Fase 1B — assimetria de modelo. O que decide é o TRABALHO: revisão e
                            // risco alto/crítico vão para a conta mais capaz; execução comum vai
                            // para a mais barata que atenda. O orçamento do card é a fonte quando
                            // existe (ReviewDepth alto = mudança que merece julgamento melhor);
                            // sem orçamento, o risco do card decide.
                            PreferMostCapable: budget is { ReviewDepth: >= 2 } ||
                                string.Equals(resolution.Role, "critic", StringComparison.Ordinal) ||
                                task.Priority is "high" or "critical"),
                        resolution, dispatchTask, dispatchInstruction.Id));
                }

                if (cards.Count == 0)
                {
                    continue;
                }

                // B6/F15 em PRODUÇÃO: a árvore publicada é reindexada antes do despacho, alimenta o
                // self-map e passa pelos três consumidores. Erro do índice não vira "impacto zero":
                // o card segue explicitamente não medido. Erro de sintaxe medido, por outro lado,
                // bloqueia antes de gastar conta; divergência plano×grafo bloqueia só os cards
                // envolvidos; o raio recalcula risco e profundidade e entra no briefing auditável.
                CodeGraph? currentGraph = null;
                try
                {
                    var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl!);
                    string? sourceRevision = null;
                    try
                    {
                        using var manager = await GitWorktreeManager.OpenAsync(
                            repositoryRoot, projectControlledRoot, token);
                        sourceRevision = await manager.ResolveCommitAsync(
                            cancellationToken: token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Repositório sem Git ainda pode ser indexado, mas não oferece revisão estável
                        // para cache. A derivação abaixo permanece a fonte da verdade desse ciclo.
                    }

                    var cached = sourceRevision is null
                        ? null
                        : await codeGraph.LoadCurrentAsync(
                            profile.TenantId, project.Id, sourceRevision, token);
                    if (cached is not null)
                    {
                        currentGraph = cached.Graph;
                        if (cached.Snapshot.ErrorCount > 0)
                        {
                            LogCodeDiagnosticsBlocked(
                                logger, project.Id, cached.Snapshot.ErrorCount);
                            deferred += cards.Count;
                            continue;
                        }
                    }
                    else
                    {
                        var derivation = await codeGraph.DeriveAndStoreAsync(
                            profile.TenantId,
                            project.Id,
                            repositoryRoot,
                            sourceRevision,
                            token);
                        await codeGraph.SyncSelfMapAsync(
                            profile.TenantId, project.Id, derivation.Graph, token);
                        currentGraph = derivation.Graph;

                        var diagnostics = CodeDiagnosticsGate.Inspect(
                            derivation.Diagnostics, CodeGraphDiagnosticScope.SyntaxOnly);
                        var deterministic = CodeDiagnosticsGate.ApplyTo(
                            new LayerResult(
                                VerificationLayer.Deterministic,
                                LayerVerdict.Pass,
                                CodeDiagnosticsGate.ReasonClean),
                            diagnostics);
                        if (!LayeredVerificationPolicy.MayOccupyReviewer([deterministic]))
                        {
                            LogCodeDiagnosticsBlocked(
                                logger, project.Id, diagnostics.ErrorCount);
                            deferred += cards.Count;
                            continue;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogCodeGraphUnavailable(logger, project.Id, exception.GetType().Name);
                }

                if (currentGraph is not null)
                {
                    var validation = await ValidatePlanAgainstGraphAsync(
                        profile.TenantId, cards, plans, currentGraph, token);
                    if (!validation.DispatchAllowed)
                    {
                        var blocked = validation.Blocking
                            .SelectMany(divergence => divergence.RelatedCardId is null
                                ? [divergence.CardId]
                                : new[] { divergence.CardId, divergence.RelatedCardId })
                            .ToHashSet(StringComparer.Ordinal);
                        foreach (var divergence in validation.Blocking)
                        {
                            LogPlanGraphBlocked(
                                logger,
                                divergence.CardId,
                                divergence.Code,
                                divergence.RelatedCardId ?? "-",
                                divergence.Explanation,
                                divergence.Evidence.Count);
                        }

                        var before = cards.Count;
                        cards.RemoveAll(candidate => blocked.Contains(candidate.Card.TaskId));
                        deferred += before - cards.Count;
                        if (cards.Count == 0)
                        {
                            continue;
                        }
                    }

                    for (var index = 0; index < cards.Count; index++)
                    {
                        var candidate = cards[index];
                        var declaredRisk = ParseRisk(candidate.Task.Priority);
                        var expandedPaths = ExpandScopeClaims(
                            candidate.Resolution.ScopeClaims, currentGraph);
                        var assessment = BlastRadiusPolicy.Assess(
                            declaredRisk, currentGraph.MeasureBlastRadius(expandedPaths));
                        var effectiveRisk = RiskName(assessment.EffectiveRisk);
                        var auditedScope =
                            $"{candidate.Resolution.Card.Scope}\n\n" +
                            "## Impacto calculado antes do despacho\n\n" +
                            $"{assessment.Justification}\n" +
                            $"Profundidade de revisão exigida: {assessment.ReviewDepth}.";
                        var adjustedResolution = candidate.Resolution with
                        {
                            Card = candidate.Resolution.Card with
                            {
                                Scope = auditedScope,
                                RiskTier = effectiveRisk
                            }
                        };
                        cards[index] = (
                            candidate.Card,
                            adjustedResolution,
                            candidate.Task,
                            candidate.InstructionVersionId);
                        LogBlastRadiusAssessed(
                            logger,
                            candidate.Task.Id,
                            assessment.DeclaredRisk,
                            assessment.EffectiveRisk,
                            assessment.ReviewDepth,
                            assessment.Measured);
                    }
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
                        // A hora do CARD, não do relógio: passar `now` para todos zerava o
                        // desempate por espera e deixava a fila decidir por ordem de leitura.
                        candidate.Task.CreatedAt);
                }

                var scale = ScaleGate.Dispatch(
                    queue,
                    Math.Max(1, settings.AutoDispatchMaxConcurrent),
                    // LiveRunCount já inclui as tentativas admitidas acima neste mesmo ciclo. Somar
                    // `dispatched` outra vez contava cada slot novo em dobro e reduzia a capacidade
                    // global artificialmente conforme o laço avançava entre projetos.
                    orchestrator.LiveRunCount);
                var admitted = new HashSet<string>(
                    scale.DispatchedWorkerCards.Concat(scale.DispatchedCriticCards),
                    StringComparer.Ordinal);
                deferred += scale.DeferredCount;

                // Card cortado pelo TETO GLOBAL virava só um contador. Quem investiga via o card
                // ser avaliado e depois desaparecer — nem despachado, nem adiado, nem uma linha
                // dizendo por quê. Foi assim que dois entregáveis de Arquitetura ficaram treze
                // horas invisíveis em 03/08/2026. O corte é legítimo; o silêncio não era.
                var liveRuns = orchestrator.LiveRunCount;
                var ceiling = Math.Max(1, settings.AutoDispatchMaxConcurrent);
                foreach (var cut in cards.Where(entry => !admitted.Contains(entry.Card.TaskId)))
                {
                    LogCardHeldByGlobalCeiling(logger, cut.Card.TaskId, liveRuns, ceiling);
                }

                if (admitted.Count == 0)
                {
                    continue;
                }

                // Ordem por ESPERA também aqui. O portão de escopo roda depois do planejador, e
                // cards de uma mesma fase reivindicam os mesmos caminhos — só um passa por vez.
                // Quem decide qual é a ordem que chega ao planejador: sem isto, a fila do teto
                // global era envelhecida mas o planejador reordenava por ordem de leitura, e os
                // dois entregáveis mais antigos perdiam o escopo em toda rodada. O planejador
                // reordena por prioridade (estável), então entrar ordenado por idade resulta em
                // "urgência primeiro, e entre iguais, quem espera há mais tempo".
                var planningCards = cards
                    .Where(entry => admitted.Contains(entry.Card.TaskId))
                    .OrderBy(entry => entry.Task.CreatedAt)
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
                // INVARIANTE: nenhum card sai de um ciclo sem desfecho registrado.
                //
                // Todo card avaliado tem de terminar a rodada despachado, adiado com motivo, ou
                // retido pelo teto — e dito em voz alta. Em 03/08/2026 dois entregáveis de
                // Arquitetura sumiram entre a avaliação e o despacho: o log mostrava "impacto do
                // card" e nada depois, e não havia como saber se o card estava andando, parado ou
                // esquecido. Um card que desaparece em silêncio é indistinguível de um card que
                // ninguém pediu, e foi assim que treze horas passaram sem ninguém notar.
                var accountedFor = plan.Dispatch.Select(item => item.Card.TaskId)
                    .Concat(plan.Deferred.Select(item => item.Card.TaskId))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var missing in planningCards.Where(card => !accountedFor.Contains(card.TaskId)))
                {
                    LogCardVanishedFromCycle(logger, missing.TaskId, missing.Role);
                }

                deferred += plan.Deferred.Count;
                foreach (var deferral in plan.Deferred)
                {
                    token.ThrowIfCancellationRequested();
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
                    token.ThrowIfCancellationRequested();
                    var entry = cards.First(candidate => candidate.Card.TaskId == decision.Card.TaskId);
                    if (orchestrator.HasLiveScopeConflict(
                            project.Id, entry.Resolution.ScopeClaims))
                    {
                        deferred++;
                        LogCardScopeDeferred(logger, entry.Task.Id);
                        continue;
                    }

                    // Contenção de PERFIL é infraestrutura, exatamente como o escopo ocupado logo
                    // acima — e por isso pertence ao adiamento, não ao orçamento de rodadas do
                    // card. Enquanto a pergunta só era feita depois de a tentativa durável
                    // existir, cada rodada contra um perfil ocupado gastava uma tentativa de
                    // quatro milissegundos, sem motivo registrado, e o detector de parede acusava
                    // "sem motivo registrado pelo executor" sobre uma causa que o sistema
                    // conhecia. O backoff de despacho recusado continua sendo o teto: adiar sem
                    // teto trocaria contenção por espera infinita.
                    if (!orchestrator.HasProfileCapacity(decision.AccountAlias))
                    {
                        deferred++;
                        _dispatchBackoff[entry.Task.Id] = clock.UtcNow.Add(RejectedDispatchBackoff);
                        LogCardProfileBusyDeferred(
                            logger, entry.Task.Id, decision.AccountAlias);
                        continue;
                    }

                    var route = await catalog.GetAgentAsync(
                        profile.TenantId, project.ChiefAgentId, token);
                    var account = accounts.Get(decision.AccountAlias);
                    var executorProfile = ExecutorCatalog.Find(account?.ExecutorId ?? string.Empty);
                    var preferredModel = route?.ModelId is { Length: > 0 } modelId
                        ? (await providerCatalog.GetModelAsync(profile.TenantId, modelId, token))?.Name
                        : null;
                    var effort = route?.Effort is { Length: > 0 } e &&
                        executorProfile?.Capabilities is { SupportsEffort: true } caps &&
                        caps.EffortValues.Contains(e, StringComparer.OrdinalIgnoreCase)
                        ? e
                        : null;

                    var routing = await providerRouting.RouteAndAuditAsync(
                        profile.TenantId,
                        project.Id,
                        decision,
                        preferredModel,
                        routingNow,
                        token);
                    if (await LaunchAsync(
                            profile.TenantId, profile.Id, project, entry.Resolution, decision.AccountAlias,
                            routing.SelectedModel, effort, entry.Task, entry.InstructionVersionId, personas,
                            catalog, projectControlledRoot,
                            string.Equals(
                                decision.ReasonCode, "chief.reinforcement_dispatched", StringComparison.Ordinal),
                            board, chain, token))
                    {
                        dispatched++;
                        dispatchCounts[project.Id] = dispatchCounts.GetValueOrDefault(project.Id) + 1;
                    }
                }
            }

        }

        return (dispatched, deferred);
    }

    public sealed record AcceptedDocumentReference(
        string DocumentId,
        string Title,
        string? PhaseName,
        string? TemplateCode,
        int Version,
        string VersionId,
        string CatalogPath,
        string ContentHash);

    private const string AcceptedDocumentsHeading =
        "\n\n## Contexto documental aceito no momento do despacho\n";
    private const string AcceptedDocumentsStart =
        "<!-- poseidon:accepted-documents:start";
    private const string AcceptedDocumentsEnd =
        "<!-- poseidon:accepted-documents:end -->";

    private static async Task<IReadOnlyList<AcceptedDocumentReference>>
        LoadAcceptedDocumentReferencesAsync(
            string tenantId,
            string projectId,
            IDocumentCatalogStore catalog,
            CancellationToken token)
    {
        var documents = (await catalog.ListDocumentsAsync(
                tenantId, projectId, null, 500, token))
            .Where(document =>
                string.Equals(document.State, "approved", StringComparison.Ordinal) &&
                !document.Inconsistent)
            .OrderBy(document => PhaseOrder(document.PhaseName) ?? int.MaxValue)
            .ThenBy(document => document.Title, StringComparer.Ordinal)
            .ThenBy(document => document.Id, StringComparer.Ordinal)
            .ToArray();
        var accepted = new List<AcceptedDocumentReference>(documents.Length);
        foreach (var document in documents)
        {
            var current = (await catalog.ListVersionsAsync(
                    tenantId, document.Id, null, 500, token))
                .SingleOrDefault(version => version.Version == document.CurrentVersion)
                ?? throw new InvalidOperationException(
                    $"Approved document {document.Id} has no current immutable version.");
            accepted.Add(new AcceptedDocumentReference(
                document.Id,
                document.Title,
                document.PhaseName,
                document.TemplateCode,
                current.Version,
                current.Id,
                current.CatalogPath,
                current.ContentHash));
        }

        return accepted;
    }

    /// <summary>
    /// Congela no briefing imutável as versões documentais aceitas que existiam NO DESPACHO.
    /// O bloco é substituível e leva checksum: retry sem mudança não cria versão nova; documento
    /// aprovado depois produz uma nova instrução, preservando exatamente o contexto recebido.
    /// </summary>
    public static string EnrichInstructionWithAcceptedDocuments(
        string instruction,
        string? taskPhaseName,
        IReadOnlyList<AcceptedDocumentReference> documents)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(documents);

        var baseInstruction = RemoveAcceptedDocumentsBlock(instruction).TrimEnd();
        var taskPhaseOrder = PhaseOrder(taskPhaseName);
        var relevant = documents
            .Where(document =>
            {
                if (taskPhaseOrder is null)
                {
                    return true;
                }

                var documentOrder = PhaseOrder(document.PhaseName);
                return documentOrder is not null && documentOrder <= taskPhaseOrder;
            })
            .OrderBy(document => PhaseOrder(document.PhaseName) ?? int.MaxValue)
            .ThenBy(document => document.Title, StringComparer.Ordinal)
            .ThenBy(document => document.DocumentId, StringComparer.Ordinal)
            .ToArray();
        if (relevant.Length == 0)
        {
            return baseInstruction;
        }

        var manifest = new System.Text.StringBuilder();
        foreach (var document in relevant)
        {
            manifest.Append("- ").Append(SingleLine(document.Title))
                .Append(" — documento `").Append(document.DocumentId)
                .Append("`, fase `").Append(SingleLine(document.PhaseName ?? "sem fase"))
                .Append("`, template `").Append(SingleLine(document.TemplateCode ?? "não informado"))
                .Append("`, versão ").Append(document.Version)
                .Append(" (`").Append(document.VersionId)
                .Append("`), catálogo `").Append(SingleLine(document.CatalogPath))
                .Append("`, SHA-256 `").Append(document.ContentHash).Append("`.\n");
        }

        var checksum = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(manifest.ToString())));
        var enriched = baseInstruction + AcceptedDocumentsHeading +
            $"{AcceptedDocumentsStart} checksum={checksum} -->\n" +
            "Estas são as versões canônicas aprovadas disponíveis para este trabalho. " +
            "Localize em `docs/` o arquivo correspondente (o hash confirma o conteúdo), cite " +
            "a versão usada e não substitua uma decisão aceita por memória ou suposição.\n" +
            manifest + AcceptedDocumentsEnd + "\n";
        if (enriched.Length > 100_000)
        {
            throw new InvalidOperationException(
                "The immutable delegation package exceeds the supported instruction size.");
        }

        return enriched;
    }

    private static string RemoveAcceptedDocumentsBlock(string instruction)
    {
        var heading = instruction.IndexOf(AcceptedDocumentsHeading, StringComparison.Ordinal);
        if (heading < 0)
        {
            return instruction;
        }

        var start = instruction.IndexOf(AcceptedDocumentsStart, heading, StringComparison.Ordinal);
        var end = instruction.IndexOf(AcceptedDocumentsEnd, start < 0 ? heading : start,
            StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            // Um bloco incompleto não é apagado em silêncio; manter o conteúdo faz o limite de
            // tamanho/critic detectar a corrupção em vez de esconder evidência.
            return instruction;
        }

        end += AcceptedDocumentsEnd.Length;
        return string.Concat(instruction.AsSpan(0, heading), instruction.AsSpan(end));
    }

    private static int? PhaseOrder(string? phaseName)
    {
        if (string.IsNullOrWhiteSpace(phaseName))
        {
            return null;
        }

        var separator = phaseName.IndexOf('-', StringComparison.Ordinal);
        return separator > 0 && int.TryParse(
            phaseName.AsSpan(0, separator),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static string SingleLine(string value) =>
        value.Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// Ordem justa e estável: menos slots consumidos primeiro; entre projetos igualmente atendidos,
    /// a atividade de negócio mais recente tem precedência e o id fecha o desempate.
    /// </summary>
    public static IReadOnlyList<ProjectRecord> OrderProjectsForDispatch(
        IReadOnlyList<ProjectRecord> projects,
        IReadOnlyDictionary<string, int> dispatchCounts)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(dispatchCounts);
        return projects
            .OrderBy(project => dispatchCounts.GetValueOrDefault(project.Id))
            .ThenByDescending(project => project.LastActivityAt)
            .ThenBy(project => project.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<PlanGraphValidation> ValidatePlanAgainstGraphAsync(
        string tenantId,
        IReadOnlyList<(
            ChiefCard Card,
            ChiefCardResolution Resolution,
            BoardTaskRecord Task,
            string InstructionVersionId)> cards,
        IDemandPlanStore plans,
        CodeGraph graph,
        CancellationToken token)
    {
        var scopes = cards
            .Select(candidate => new CardScope(
                candidate.Card.TaskId,
                candidate.Task.Title,
                ExpandScopeClaims(candidate.Resolution.ScopeClaims, graph)))
            .ToArray();
        var edges = new List<CardDependencyEdge>();
        foreach (var demandGroup in cards
                     .Where(candidate => candidate.Task.DemandId is not null)
                     .GroupBy(candidate => candidate.Task.DemandId!, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var plan = await plans.GetByDemandAsync(tenantId, demandGroup.Key, token);
            if (plan?.MaterializedAt is null)
            {
                continue;
            }

            var taskByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in demandGroup)
            {
                taskByCode.TryAdd(
                    DemandDecompositionPlanner.CodeOf(candidate.Task.Title),
                    candidate.Task.Id);
            }

            foreach (var card in plan.Cards)
            {
                if (!taskByCode.TryGetValue(
                        DemandDecompositionPlanner.CodeOf(card.ProposedTitle),
                        out var consumerId))
                {
                    continue;
                }

                foreach (var dependency in card.Dependencies)
                {
                    if (taskByCode.TryGetValue(dependency, out var providerId))
                    {
                        edges.Add(new CardDependencyEdge(
                            providerId, consumerId, dependency));
                    }
                }
            }
        }

        return PlanGraphValidationPolicy.Validate(scopes, edges, graph);
    }

    /// <summary>
    /// O executor recebe claims (arquivo ou prefixo/**), enquanto o grafo mede arquivos concretos.
    /// Expandir contra os paths que o índice realmente conhece evita tanto impacto-zero por glob
    /// quanto inventar cobertura para frontend/arquivos que o índice C# não viu.
    /// </summary>
    private static IReadOnlyList<string> ExpandScopeClaims(
        IReadOnlyList<string> claims,
        CodeGraph graph)
    {
        var indexedFiles = graph.Nodes
            .Select(node => node.FilePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expanded = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawClaim in claims)
        {
            var claim = rawClaim.Trim().Replace('\\', '/').TrimStart('/');
            if (claim.EndsWith("/**", StringComparison.Ordinal))
            {
                var prefix = claim[..^3].TrimEnd('/');
                var matched = indexedFiles
                    .Where(path =>
                        string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matched.Length > 0)
                {
                    expanded.UnionWith(matched);
                }
                else
                {
                    // O claim continua visível como NÃO indexado; jamais vira impacto zero.
                    expanded.Add(claim);
                }

                continue;
            }

            expanded.Add(claim);
        }

        return [.. expanded];
    }

    private static RiskTier ParseRisk(string risk) =>
        risk.Trim().ToLowerInvariant() switch
        {
            "low" => RiskTier.Low,
            "high" => RiskTier.High,
            "critical" => RiskTier.Critical,
            _ => RiskTier.Medium
        };

    private static string RiskName(RiskTier risk) =>
        risk.ToString().ToLowerInvariant();

    /// <summary>
    /// Vereditos de review que representam uma REVISÃO real executada (e portanto podem ser
    /// aplicados na cadeia). Qualquer outro código é falha de infraestrutura/conta e vira
    /// retry com backoff — nunca rejeita o trabalho do ator por culpa do crítico.
    /// </summary>
    private static readonly HashSet<string> AppliableReviewReasons = new(
        ["critic.pass", "critic.fail", "critic.pass_contradicted_by_findings"],
        StringComparer.Ordinal);

    /// <summary>
    /// O código com que um gate DETERMINÍSTICO reprova a entrega. Existe como constante porque a
    /// regra que o governa é invisível no ponto de uso: um código fora de
    /// <see cref="AppliableReviewReasons"/> não vira reprovação — vira adiamento eterno.
    /// </summary>
    internal const string DeterministicRejectionReasonCode = "critic.fail";

    internal static bool IsAppliableReviewReason(string reasonCode) =>
        AppliableReviewReasons.Contains(reasonCode);

    /// <summary>
    /// D1 — este card é um parecer do Conselho, e por isso não passa por revisão independente.
    ///
    /// O discriminador é o tipo <c>council</c>. Em 04/08/2026 a auditoria macro exigiu um tipo
    /// próprio (F-03): o card de revisão genérico <c>revisao</c> não distingue um parecer do
    /// Conselho de uma revisão técnica comum, e exigir segundo par de olhos no parecer do Conselho
    /// cria uma regressão infinita que consome o próprio elenco de críticos. O tipo <c>council</c>
    /// nasce em <c>WorkflowPhaseDriver.CreateCouncilCardAsync</c> e é o único que dispara a
    /// aprovação direta.
    ///
    /// O tipo <c>revisao</c> continua reconhecido como compatibilidade para cards criados antes da
    /// migration 0122; cards novos usam <c>council</c>.
    /// </summary>
    internal static bool IsCouncilOpinionCard(BoardTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return string.Equals(task.CardType, "council", StringComparison.Ordinal)
            || string.Equals(task.CardType, "revisao", StringComparison.Ordinal);
    }

    /// <summary>As três linhas de roteamento no topo de toda instrução, na ordem em que nascem.</summary>
    private static readonly string[] InstructionHeaderPrefixes =
    [
        "Capacidade de execução autorizada:",
        "Especialidade exigida:",
        "Tipo de card:",
    ];

    /// <summary>
    /// Troca o cabeçalho de roteamento pelo estado ATUAL, preservando o corpo intacto.
    ///
    /// Papel, persona e tipo de card são derivados do card a cada despacho; mantê-los na
    /// instrução é conveniência para o executor, não registro histórico. Carregá-los adiante
    /// transformava um erro de roteamento da versão 1 em erro permanente — ver OPS-069.
    ///
    /// Regras de segurança, nesta ordem de importância: o CORPO nunca é alterado, porque é onde
    /// está o trabalho; um texto sem cabeçalho reconhecível ganha um, em vez de perder linhas por
    /// palpite; e a remoção só acontece nas linhas do TOPO, para que uma menção a "Tipo de card:"
    /// no meio de um achado do crítico não seja engolida.
    /// </summary>
    internal static string RebuildInstructionHeader(
        string previousBody, string role, string personaKey, string cardType)
    {
        ArgumentNullException.ThrowIfNull(previousBody);
        var header =
            $"Capacidade de execução autorizada: {role}\n" +
            $"Especialidade exigida: {personaKey}\n" +
            $"Tipo de card: {cardType}";

        var lines = previousBody.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var skip = 0;
        while (skip < lines.Length &&
            InstructionHeaderPrefixes.Any(prefix =>
                lines[skip].StartsWith(prefix, StringComparison.Ordinal)))
        {
            skip++;
        }

        // Nenhuma linha de cabeçalho reconhecida: o corpo inteiro é preservado e ganha o
        // cabeçalho novo. Nunca se remove por suposição.
        if (skip == 0)
        {
            return $"{header}\n\n{previousBody}";
        }

        // A linha em branco que separava cabeçalho de corpo pertence ao cabeçalho, não ao corpo.
        while (skip < lines.Length && lines[skip].Length == 0)
        {
            skip++;
        }

        var body = string.Join('\n', lines.Skip(skip));
        return body.Length == 0 ? header : $"{header}\n\n{body}";
    }

    private static readonly HashSet<string> CorrectableDocumentPublicationFailures = new(
        [
            "document.artifact_missing",
            "document.artifact_ambiguous",
            "document.delivery_placeholder",
            "document.template_unknown",
            "document.template_not_satisfied",
        ],
        StringComparer.Ordinal);

    private static readonly TimeSpan ReviewRetryBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Teto de adiamentos consecutivos por falha de INFRAESTRUTURA na revisão de uma mesma
    /// tentativa. Adiar é a resposta certa para uma indisponibilidade momentânea e a resposta
    /// errada para uma permanente: sem teto, o mesmo card re-invoca o crítico a cada janela de
    /// backoff para sempre, acumula falhas consecutivas na conta do revisor, abre o circuito de
    /// capacidade e passa a bloquear a revisão de TODOS os projetos — sem nunca avisar ninguém.
    /// Estourado o teto, o card vira impedimento escalado e sai da fila de retry.
    /// </summary>
    internal const int MaximumReviewInfrastructureFailures = 4;

    /// <summary>Backoff em memória por tentativa para reviews com falha de infraestrutura.</summary>
    private readonly Dictionary<string, DateTimeOffset> _reviewBackoff = new(StringComparer.Ordinal);

    /// <summary>
    /// Adiamentos consecutivos por tentativa. Zera quando um veredito real é aplicado — o que
    /// conta é a sequência SEM progresso, não o total histórico.
    /// </summary>
    private readonly Dictionary<string, int> _reviewInfrastructureFailures =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Motivo que significa "não havia ninguém para revisar", e não "a revisão falhou". A distinção
    /// existe porque o teto acima mede FALHA, e ausência de revisor não é falha de nada: é espera.
    /// Um crítico que foi eleito e cujo executor quebrou (`critic.executor_unavailable`) continua
    /// contando — ali houve tentativa real, e insistir nela sem limite é o laço que o teto impede.
    /// </summary>
    private static readonly HashSet<string> ReviewerShortageReasons = new(
        ["critic.none_available"],
        StringComparer.Ordinal);

    /// <summary>
    /// Quanto tempo se espera por um revisor que EXISTE no elenco antes de admitir que ele não vem.
    ///
    /// O teto de quatro adiamentos com backoff de cinco minutos dava vinte minutos — menos que
    /// qualquer janela de cota — e depois disso o card era escalado como se a ENTREGA tivesse
    /// problema. Foi o que aconteceu com os seis assentos do Conselho: a única outra conta com
    /// papel de crítico estava em resfriamento, os seis escalaram em oito minutos e continuaram
    /// escalados horas depois de o crítico voltar. Contar tentativas mede a frequência com que
    /// perguntamos; o que importa aqui é há quanto tempo ninguém pode responder.
    /// </summary>
    internal static readonly TimeSpan ReviewerShortageGrace = TimeSpan.FromHours(2);

    /// <summary>
    /// Teto absoluto da espera por revisor. Existe para que "o provedor disse que volta" não vire
    /// espera indefinida se ele disser uma data distante: passado isto, é impedimento e o dono
    /// precisa saber.
    /// </summary>
    internal static readonly TimeSpan ReviewerShortageMaximumWait = TimeSpan.FromHours(12);

    /// <summary>
    /// Este adiamento é ESPERA por um revisor que existe, ou uma falha a caminho da escalação?
    ///
    /// <paramref name="reviewerReturnsBy"/> é o que o SISTEMA já sabe sobre o retorno: nulo quando
    /// ninguém no elenco serve este ator (esperar não muda esse fato), o próprio instante atual
    /// quando há candidato sem restrição, e a janela declarada pelo provedor quando o candidato
    /// está em cota. Usar uma constante em vez dessa data repetiria o erro que a operação já
    /// pagou três vezes: o provedor DIZ quando volta, e o código decidia sem ler. Com o crítico de
    /// volta às 01:21 e uma carência fixa de duas horas, o card escalaria às 00:24 — uma hora
    /// antes da resposta existir.
    /// </summary>
    internal static bool ShouldWaitForReviewer(
        DateTimeOffset? reviewerReturnsBy,
        string reasonCode,
        DateTimeOffset waitingSince,
        DateTimeOffset now)
    {
        if (reviewerReturnsBy is not { } returnsBy ||
            !ReviewerShortageReasons.Contains(reasonCode))
        {
            return false;
        }

        // A carência mínima cobre o revisor que está apenas ocupado, sem data declarada; a janela
        // do provedor estende a espera quando ela é maior. O teto vale sobre as duas.
        var deadline = waitingSince + ReviewerShortageGrace;
        if (returnsBy > deadline)
        {
            deadline = returnsBy;
        }

        var ceiling = waitingSince + ReviewerShortageMaximumWait;
        return now < (deadline < ceiling ? deadline : ceiling);
    }

    /// <summary>
    /// Desde quando esta tentativa espera por um revisor que o elenco ainda pode fornecer. Zera
    /// junto com o backoff quando um veredito real é aplicado.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _reviewerShortageSince =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Abertura fixa do aviso de etapa concluída. É por ela — mais o nome da etapa — que um aviso
    /// já publicado é reconhecido depois de um reinício do Host.
    /// </summary>
    private const string MilestoneMarker = "Concluímos uma etapa do projeto:";

    /// <summary>
    /// Abertura COMPLETA do aviso de uma etapa específica. O reconhecimento precisa casar com esta
    /// linha inteira, não só com o nome da etapa: o aviso de uma etapa anuncia para onde o projeto
    /// segue, então o nome da PRÓXIMA etapa também aparece no texto. Procurar só pelo nome fazia o
    /// aviso da Triagem — que diz "sigo agora para 2-Descoberta" — passar por aviso já publicado da
    /// Descoberta, e a etapa seguinte fechava em silêncio.
    /// </summary>
    internal static string MilestoneHeadingFor(string phaseName) => MilestoneHeading(phaseName);

    /// <summary>Um aviso já publicado é reconhecido pela abertura COMPLETA da própria etapa.</summary>
    internal static bool MilestoneAlreadyAnnounced(
        IEnumerable<string> publishedMessages, string phaseName)
    {
        ArgumentNullException.ThrowIfNull(publishedMessages);
        var heading = MilestoneHeading(phaseName);
        return publishedMessages.Any(content =>
            content.Contains(heading, StringComparison.Ordinal));
    }

    private static string MilestoneHeading(string phaseName) =>
        $"{MilestoneMarker} **{phaseName}**.";

    /// <summary>Etapas já anunciadas nesta execução do processo.</summary>
    private readonly HashSet<string> _announcedMilestones = new(StringComparer.Ordinal);

    /// <summary>
    /// Espera antes de re-tentar um card cujo run foi RECUSADO na largada (tipicamente conflito de
    /// claim com um run vivo do mesmo escopo). Sem ela, o ciclo re-despachava o card a cada
    /// intervalo do loop: cada rodada abria uma tentativa durável, colhia a recusa, expirava a
    /// lease e recomeçava — dezenas de tentativas fantasma na cadeia, sem nenhum trabalho feito.
    /// O escopo só libera quando o run concorrente termina, o que leva minutos, não segundos.
    /// </summary>
    private static readonly TimeSpan RejectedDispatchBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailedRunRetryBackoff = TimeSpan.FromMinutes(2);

    /// <summary>Backoff em memória por CARD para despachos recusados na largada.</summary>
    private readonly Dictionary<string, DateTimeOffset> _dispatchBackoff = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs consecutivos do CARD que não produziram um único token de saída.
    ///
    /// É a resposta à segunda pergunta, e ela não é a do circuito: o circuito pergunta "esta
    /// falha é culpa do card?" e responde bem — zero token quer dizer que ninguém julgou o
    /// enunciado, então não conta. Só que "ninguém julgou" repetido para sempre é o pior
    /// desfecho possível, e era exatamente o que ficava invisível: em 03/08/2026 foram catorze
    /// tentativas em uma hora e quarenta, todas de zero token, sem circuito aberto e sem freio.
    ///
    /// Em memória de propósito: é um FREIO, não um veredito. Reinício do Host zera a contagem, e
    /// isso é aceitável — depois de um reinício vale mesmo a pena tentar de novo.
    /// </summary>
    private readonly Dictionary<string, int> _noProgressRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// Marcador estável do aviso de escalação. É o que permite reconhecer, na própria conversa,
    /// que aquele card JÁ foi levado ao dono — idempotência que sobrevive a restart.
    /// </summary>
    /// <summary>
    /// Teto de instruções de um card que escala sem nunca ter rodado. Cada replanejamento grava
    /// uma versão; passando disso, o problema não é a redação e insistir vira laço.
    /// </summary>
    private const int MaximumOperationalReplanRounds = 4;

    private const string EscalationMarker = "Preciso da sua decisão em uma parte do projeto:";

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
    /// para re-despacho. Falha permanente que chegou a executar consome rodada; cancelamento,
    /// timeout, falha transitória e falha do próprio orquestrador não consomem. Idempotente por
    /// tentativa.
    /// </summary>
    internal async Task<int> HarvestCompletedRunsAsync(
        string tenantId,
        ProjectRecord project,
        string controlledRoot,
        IWorkBoardStore board,
        IWorkChainStore chain,
        IModelInvocationStore invocations,
        CancellationToken token)
    {
        var harvested = 0;
        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, "development", null, null, "active", null, 0, 50),
            token);
        foreach (var task in page.Items)
        {
            token.ThrowIfCancellationRequested();
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
                // Tentativa morta — fechada pela reconciliação, que roda antes dos portões de
                // produção e alcança também projeto pausado/manual.
                continue;
            }

            if (snapshot.Status == AgentRunStatus.Completed)
            {
                var branch = $"task/agent-run-{running.Id.ToLowerInvariant()}";
                // O orquestrador colhe antes de remover a worktree e grava o SHA no workspace.
                // A colheita aqui permanece como compensação para runs legados/interrompidos,
                // mas a ausência da pasta não pode apagar a evidência já persistida.
                var deliveryCommit = await TryHarvestWorktreeAsync(
                    project, controlledRoot, running.Id, branch, token)
                    ?? snapshot.Workspace?.CommitSha;
                var evidence = new List<WorkEvidenceInput>
                {
                    new(UlidValue.New(now).ToString(), $"agent-run:{running.Id}"),
                    new(UlidValue.New(now.AddTicks(1)).ToString(), $"git-branch:{branch}"),
                };
                if (!string.IsNullOrWhiteSpace(deliveryCommit))
                {
                    evidence.Add(new WorkEvidenceInput(
                        UlidValue.New(now.AddTicks(2)).ToString(), $"git-commit:{deliveryCommit}"));
                }
                // A tentativa também carrega o consumo medido: sem esta projeção o quadro, a
                // Central de Entregas e a auditoria leem duração, tokens e custo zerados mesmo
                // com trabalho real feito. Métrica não exposta pelo executor fica nula (o store
                // preserva o valor anterior) em vez de virar zero.
                //
                // `snapshot.Execution` vem da SESSÃO VIVA e é nulo sempre que a colheita acontece
                // num ciclo posterior ao término do processo — o caso comum. Por isso a fonte
                // primária é o ledger durável de invocações, que sobrevive ao fim da sessão e ao
                // reinício do Host.
                var usage = await ReadAttemptUsageAsync(
                    tenantId, task.Id, running.Id, snapshot.Execution, invocations, token);
                var completeCommand = new WorkAttemptCompleteCommand(
                    tenantId, task.BackingSolicitationId, task.Id, running.Id, task.Version,
                    evidence,
                    $"chief-loop-complete:{running.Id}", now,
                    usage);
                // F-03: pareceres do Conselho são isentos de revisão independente — a consolidação
                // do Conselho já é o controle. Exigir segundo par de olhos cria uma regressão
                // infinita que consome o próprio elenco de críticos.
                var completed = string.Equals(task.CardType, "council", StringComparison.OrdinalIgnoreCase)
                    ? await chain.CompleteAndApproveAttemptAsync(completeCommand, token)
                    : await chain.CompleteAttemptAsync(completeCommand, token);
                if (completed.Status is WorkChainMutationStatus.Applied
                    or WorkChainMutationStatus.IdempotentReplay)
                {
                    harvested++;
                    LogRunHarvested(logger, task.Id, running.Id);
                }
            }

            // Falha/cancelamento e órfã sem workspace → reconciliação (acima no ciclo).
            // Accepted/Running → ainda em voo; nada a fazer neste ciclo.
        }

        return harvested;
    }

    /// <summary>
    /// Fecha tentativas cujo processo MORREU, devolvendo o card à fila.
    ///
    /// Roda ANTES dos portões de produção do ciclo (pausa, raiz controlada, modo manual) porque
    /// reconciliar trabalho já morto não é produzir trabalho novo. Enquanto isto vivia dentro da
    /// colheita, um projeto pausado — ou em modo manual — nunca fechava a tentativa de um processo
    /// derrubado; e como a recuperação de workspace já havia liberado a worktree (some das listas,
    /// que filtram `released_at IS NULL`) e `ux_work_attempts_one_active` proíbe uma segunda
    /// tentativa viva no mesmo card, o card ficava travado de forma IRREVERSÍVEL. Nem despausar
    /// resolvia.
    ///
    /// Só toca no que está morto: run concluído continua sendo colhido pela colheita, com
    /// evidência e consumo medido.
    /// </summary>
    /// <summary>
    /// Modo do projeto sem deixar a exceção do resolvedor derrubar o ciclo. Não saber o modo é
    /// motivo para NÃO produzir — e, portanto, para a reconciliação assumir o registro.
    /// </summary>
    private static async Task<bool> ResolvesToAutonomousAsync(
        Harness.Persistence.Abstractions.Workflows.IWorkflowCatalogStore workflows,
        string tenantId,
        ProjectRecord project,
        CancellationToken token)
    {
        try
        {
            var mode = await Harness.Host.Workflows.ProjectOperationModeResolver.ResolveAsync(
                workflows, tenantId, project.Id, project.OperationMode, token);
            return mode != Harness.Modules.Workflows.Application.ProjectOperationMode.Manual;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ReconcileDeadAttemptsAsync(
        string tenantId,
        ProjectRecord project,
        IWorkBoardStore board,
        IWorkChainStore chain,
        bool willHarvest,
        CancellationToken token)
    {
        // Paginação até o FIM. Uma página única de 50 significava que, num projeto com mais de
        // cinquenta cards ativos ao mesmo tempo, os excedentes nunca eram reconciliados — e essa
        // falha é silenciosa por natureza: o card fica preso em `running` para sempre e nada no
        // log diz que ele sequer chegou a ser olhado. O teto de páginas existe apenas para que
        // um erro de paginação não vire laço infinito.
        var page = await PagedScan.CollectAsync<BoardTaskRecord>(
            async (offset, size) =>
            {
                var current = await board.PageTasksAsync(
                    tenantId,
                    new BoardTaskPageQuery(
                        project.Id, null, null, "development", null, null, "active", null,
                        offset, size),
                    token);
                return (current.Items, current.Total);
            },
            token);

        // Diagnóstico da própria reconciliação. Três tentativas ficaram presas em `running` por
        // até seis dias sem produzir UMA linha de log: nenhuma exceção, nenhum requeue, nada. Não
        // se conserta o que não se enxerga, e a dedução estática já se esgotou neste caso — então
        // o caminho passa a declarar quantos cards viu e quantos qualificou.
        var running = page.Items.Count(item =>
            string.Equals(item.InternalState, "running", StringComparison.Ordinal));
        LogReconcileScan(logger, project.Id, page.Items.Count, page.Total, running);

        foreach (var task in page.Items)
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(task.InternalState, "running", StringComparison.Ordinal))
            {
                continue;
            }

            // ISOLAMENTO POR CARD. Um card só pode envenenar a si mesmo.
            //
            // Duas vezes seguidas um único card derrubou o ciclo INTEIRO do projeto: primeiro por
            // `ArgumentException` do validador, depois por `IdempotencyConflictException` — a chave
            // de idempotência é estável, mas o comando carrega `OccurredAt`, então um recibo de
            // mutação REJEITADA grava um hash que nenhum retry posterior reproduz, e o conflito
            // passa a ser permanente. Com o `try` só em volta do ciclo todo, esse card virava uma
            // pílula de veneno: colheita, review, integração e avanço de fase morriam junto, e o
            // projeto congelava indefinidamente.
            //
            // Aqui a falha fica contida no card que a causou e o restante do projeto continua.
            try
            {
                await ReconcileDeadAttemptAsync(tenantId, task, board, chain, willHarvest, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFollowUpFailure(logger, exception, task.Id, exception.GetType().Name);
            }
        }
    }

    private async Task ReconcileDeadAttemptAsync(
        string tenantId,
        BoardTaskRecord task,
        IWorkBoardStore board,
        IWorkChainStore chain,
        bool willHarvest,
        CancellationToken token)
    {
        var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
        var running = attempts.FirstOrDefault(attempt =>
            string.Equals(attempt.State, "running", StringComparison.Ordinal));
        if (running is null)
        {
            // O card diz `running` mas nenhuma tentativa dele se diz `running`. É um estado
            // inconsistente que a reconciliação atual não resolve e, calada, parecia sucesso.
            LogReconcileNoRunningAttempt(logger, task.Id, attempts.Count);
            return;
        }

        var snapshot = await orchestrator.GetAsync(tenantId, running.Id, token);
        var now = clock.UtcNow;
        LogReconcileSubject(logger, task.Id, running.Id, snapshot?.Status);

        // A DECISÃO é da política pura; aqui fica só a execução dela. Cada ramo custou um card
        // travado, e separar os dois permite verificar todos sem subir Host, banco e Git.
        var decision = AttemptReconciliationPolicy.Decide(new AttemptReconciliationFacts(
            HasWorkspace: snapshot is not null,
            RunCompleted: snapshot?.Status == AgentRunStatus.Completed,
            RunDead: snapshot?.Status is AgentRunStatus.Failed or AgentRunStatus.Cancelled,
            Age: now - running.StartedAt,
            RichHarvestWillRun: willHarvest));

        if (decision == AttemptReconciliationAction.None)
        {
            return;
        }

        if (decision == AttemptReconciliationAction.ExpireOrphan)
        {
            // Tentativa SEM workspace: o orquestrador nunca aceitou o run (órfã de uma
            // compensação perdida — ex.: processo caiu entre o start da cadeia e o aceite).
            _ = await chain.ExpireAttemptLeaseAsync(
                new WorkAttemptLeaseExpiredCommand(
                    tenantId, task.BackingSolicitationId, task.Id, running.Id, task.Version,
                    $"chief-loop-orphan:{running.Id}", now),
                token);
            LogRunRequeued(logger, task.Id, running.Id, "orphan");
            return;
        }

        if (snapshot is null)
        {
            return;
        }

        if (decision == AttemptReconciliationAction.RecordCompleted)
        {
            // Run que TERMINOU. Se a colheita rica vai rodar neste ciclo, é ela quem registra —
            // ela harmoniza a worktree e mede o consumo. Se NÃO vai (projeto pausado, manual ou
            // fora da raiz controlada), o registro é feito aqui, porque pausar deve impedir
            // trabalho novo e não apagar trabalho já feito: sem isto o card fica preso para
            // sempre com uma entrega pronta que ninguém colheu.
            //
            // Só estado DURÁVEL é usado — o SHA já persistido no workspace. Nada de tocar o
            // sistema de arquivos: a worktree pode estar fora da raiz controlada, e reconciliar
            // não é licença para atravessar essa fronteira.
            {
                var deliveryCommit = snapshot.Workspace?.CommitSha;
                var evidence = new List<WorkEvidenceInput>
                {
                    new(UlidValue.New(now).ToString(), $"agent-run:{running.Id}"),
                    new(UlidValue.New(now.AddTicks(1)).ToString(),
                        $"git-branch:task/agent-run-{running.Id.ToLowerInvariant()}"),
                };
                if (!string.IsNullOrWhiteSpace(deliveryCommit))
                {
                    evidence.Add(new WorkEvidenceInput(
                        UlidValue.New(now.AddTicks(2)).ToString(), $"git-commit:{deliveryCommit}"));
                }

                var reconcileCommand = new WorkAttemptCompleteCommand(
                    tenantId, task.BackingSolicitationId, task.Id, running.Id, task.Version,
                    evidence, $"chief-loop-reconcile-complete:{running.Id}", now, null);
                // F-03: pareceres do Conselho são isentos de revisão independente.
                var completed = string.Equals(task.CardType, "council", StringComparison.OrdinalIgnoreCase)
                    ? await chain.CompleteAndApproveAttemptAsync(reconcileCommand, token)
                    : await chain.CompleteAttemptAsync(reconcileCommand, token);
                if (completed.Status is WorkChainMutationStatus.Applied
                    or WorkChainMutationStatus.IdempotentReplay)
                {
                    LogRunHarvested(logger, task.Id, running.Id);
                }
            }

            return;
        }

        // Devolve o card à fila (`ready`) para re-despacho. Só uma falha PERMANENTE do
        // executor representa rodada de trabalho gasta; problemas transitórios/infraestrutura
        // e cancelamentos continuam recuperáveis sem queimar o orçamento anti-loop.
        var countsTowardRoundBudget = CountsFailedRunTowardRoundBudget(snapshot);

        // O MOTIVO é gravado sempre que o run realmente falhou, mesmo transitório. Ele não
        // é o orçamento de rodadas (acima) — é o que torna a falha VISÍVEL para o circuito
        // do card. Sem ele, uma falha de execução chegava ao quadro como `cancelled` sem
        // motivo, indistinguível de um cancelamento do operador ou de um reinício do Host:
        // o circuito não a contava, e o mesmo card era redespachado indefinidamente. Foi
        // exatamente o que aconteceu — nove tentativas seguidas no mesmo card, todas
        // transitórias, e o circuito parado em uma.
        var failureReason = ResolveAttemptFailureReason(snapshot, countsTowardRoundBudget);
        var expired = await chain.ExpireAttemptLeaseAsync(
            new WorkAttemptLeaseExpiredCommand(
                tenantId, task.BackingSolicitationId, task.Id, running.Id, task.Version,
                $"chief-loop-expire:{running.Id}", now,
                countsTowardRoundBudget,
                failureReason),
            token);
        if (expired.Status is WorkChainMutationStatus.Applied
            or WorkChainMutationStatus.IdempotentReplay)
        {
            // Progresso zera o freio; a ausência dele acumula. A contagem é do CARD, não da
            // conta: a mesma parede pode aparecer em contas diferentes, e foi o que aconteceu.
            if (ProducedOutput(snapshot))
            {
                _noProgressRuns.Remove(task.Id);
            }
            else
            {
                _noProgressRuns[task.Id] = _noProgressRuns.GetValueOrDefault(task.Id) + 1;
            }

            var noProgress = _noProgressRuns.GetValueOrDefault(task.Id);
            var delay = noProgress > 1
                ? NoProgressBackoff(noProgress)
                : RetryDelayAfterRun(snapshot) ?? FailedRunRetryBackoff;
            if (noProgress > 1)
            {
                LogNoProgressBrake(logger, task.Id, noProgress, (int)delay.TotalSeconds);
            }

            _dispatchBackoff[task.Id] = now.Add(delay);
        }

        LogRunRequeued(logger, task.Id, running.Id, snapshot.Status.ToString());
    }

    /// <summary>
    /// Resposta única a um review que NÃO pôde ser executado por infraestrutura. Adia com backoff
    /// enquanto houver crédito de tentativas; estourado o teto, escala o card com motivo tipado —
    /// o trabalho do ator permanece íntegro em `awaiting_review` e nada aqui reprova o executor.
    /// </summary>
    /// <returns><see langword="true"/> quando o card foi escalado e sai da fila de retry.</returns>
    private async Task<bool> DeferOrEscalateReviewAsync(
        string tenantId,
        BoardTaskRecord task,
        string attemptId,
        string reasonCode,
        IWorkChainStore chain,
        DateTimeOffset now,
        CancellationToken token,
        DateTimeOffset? reviewerReturnsBy = null)
    {
        // ESPERA ≠ FALHA. Quando o elenco tem um crítico elegível que está apenas ocupado ou em
        // resfriamento, adiar não é insistir num caminho quebrado: é aguardar quem já se sabe que
        // volta. Contar isso no teto de falhas escalava o card por culpa de terceiro, e o
        // replanejamento — único caminho de volta — o encontrava com a entrega intacta e recusava
        // por estado inválido. O relógio, não o contador, é quem decide desistir: passada a
        // carência, o revisor declaradamente não veio e aí sim é impedimento.
        if (reviewerReturnsBy is not null && ReviewerShortageReasons.Contains(reasonCode))
        {
            var waitingSince = _reviewerShortageSince.TryGetValue(attemptId, out var since)
                ? since
                : now;
            _reviewerShortageSince[attemptId] = waitingSince;
            if (ShouldWaitForReviewer(reviewerReturnsBy, reasonCode, waitingSince, now))
            {
                _reviewBackoff[attemptId] = now.Add(ReviewRetryBackoff);
                return false;
            }
        }

        var failures = _reviewInfrastructureFailures.TryGetValue(attemptId, out var previous)
            ? previous + 1
            : 1;
        _reviewInfrastructureFailures[attemptId] = failures;
        if (failures < MaximumReviewInfrastructureFailures)
        {
            _reviewBackoff[attemptId] = now.Add(ReviewRetryBackoff);
            return false;
        }

        var reason =
            $"Revisão independente indisponível após {failures} tentativas ({reasonCode}). " +
            "O trabalho entregue está preservado e continua aguardando revisão; o que falhou foi " +
            "o revisor, não a entrega.";
        var receipt = await chain.EscalateUnreviewableTaskAsync(
            new WorkTaskReviewUnavailableCommand(
                tenantId, task.BackingSolicitationId, task.Id, attemptId,
                reason, $"attempt:{attemptId}", task.Version,
                $"chief-loop-review-unavailable:{attemptId}:{failures}", now),
            token);
        if (receipt.Status is not (WorkChainMutationStatus.Applied
            or WorkChainMutationStatus.IdempotentReplay))
        {
            // Corrida legítima (o card mudou de estado entre a leitura e a escalação): não insiste
            // neste ciclo e deixa o próximo reler o estado atual.
            _reviewBackoff[attemptId] = now.Add(ReviewRetryBackoff);
            return false;
        }

        LogReviewUnavailableEscalated(logger, task.Id, attemptId, reasonCode, failures);
        _reviewBackoff.Remove(attemptId);
        _reviewInfrastructureFailures.Remove(attemptId);
        _reviewerShortageSince.Remove(attemptId);
        return true;
    }

    /// <summary>Zera o histórico de adiamentos quando um veredito REAL foi aplicado.</summary>
    private void ClearReviewDeferrals(string attemptId)
    {
        _reviewBackoff.Remove(attemptId);
        _reviewInfrastructureFailures.Remove(attemptId);
        _reviewerShortageSince.Remove(attemptId);
    }

    /// <summary>
    /// Consumo medido da tentativa. A sessão viva é a fonte mais rica, mas some assim que o
    /// processo termina; o ledger de invocações é durável e cobre inclusive a colheita feita
    /// depois de um reinício do Host. Somar as invocações da tentativa é o número certo: uma
    /// tentativa pode ter mais de uma chamada (retry interno, continuação).
    /// </summary>
    private static async Task<WorkAttemptUsage?> ReadAttemptUsageAsync(
        string tenantId,
        string taskId,
        string attemptId,
        ExternalAgentRunResult? execution,
        IModelInvocationStore invocations,
        CancellationToken token)
    {
        IReadOnlyList<ModelInvocationRecord> recorded = [];
        try
        {
            recorded = [.. (await invocations.GetTaskInvocationsAsync(tenantId, taskId, token))
                .Where(entry => string.Equals(entry.AttemptId, attemptId, StringComparison.Ordinal))];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Telemetria não pode impedir a colheita de um trabalho que já ficou pronto.
        }

        return ToAttemptUsage(execution, recorded);
    }

    /// <summary>
    /// Combina sessão viva e ledger durável. Um campo permanece <see langword="null"/> quando
    /// NENHUMA das duas fontes o mediu — o store então preserva o valor anterior, para que "zero
    /// medido" nunca seja confundido com "desconhecido".
    ///
    /// Uma invocação sem uso exposto grava zero no ledger e é marcada com o sufixo
    /// <c>usage_unknown</c> no desfecho; somar esse zero como se fosse medição produziria um custo
    /// falso de US$ 0,00. Por isso tokens e custo só sobem quando existe ao menos uma invocação
    /// com uso realmente conhecido.
    /// </summary>
    public static WorkAttemptUsage? ToAttemptUsage(
        ExternalAgentRunResult? execution,
        IReadOnlyList<ModelInvocationRecord> recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        var measured = recorded
            .Where(entry => !entry.Outcome.EndsWith("|usage_unknown", StringComparison.Ordinal))
            .ToArray();

        long? durationMs = execution?.DurationMs;
        if (durationMs is null && recorded.Count > 0)
        {
            durationMs = recorded.Sum(entry => entry.DurationMs);
        }

        long? tokensInput = execution?.Usage?.InputTokens;
        long? tokensOutput = execution?.Usage?.OutputTokens;
        decimal? costUsd = execution?.Usage?.CostUsd;
        if (measured.Length > 0)
        {
            tokensInput ??= measured.Sum(entry => (long)entry.InputTokens);
            tokensOutput ??= measured.Sum(entry => (long)entry.OutputTokens);
            costUsd ??= measured.Sum(entry => entry.EstimatedCostUsd);
        }

        return durationMs is null && tokensInput is null && tokensOutput is null && costUsd is null
            ? null
            : new WorkAttemptUsage(durationMs, tokensInput, tokensOutput, costUsd);
    }

    /// <summary>
    /// Separa falha de TRABALHO de falha de INFRAESTRUTURA. Sem um resultado externo real não
    /// existe evidência de que a especialidade executou e falhou; por isso exceções internas do
    /// orquestrador não queimam rodada. Quando existe resultado, a classificação canônica decide.
    /// </summary>
    public static bool CountsFailedRunTowardRoundBudget(AgentRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != AgentRunStatus.Failed || snapshot.Execution is null)
        {
            return false;
        }

        return ClassifyRun(snapshot).Kind == AgentRunOutcomeKind.Permanent;
    }

    /// <summary>
    /// Classificação canônica de um run, incluindo a cauda de erro do executor.
    ///
    /// O diagnóstico importa porque a CLI nem sempre traduz o erro do provedor em código: cota
    /// esgotada no GLM/Z.AI chegava como `executor.exit_code_1` e era lida como instabilidade.
    /// </summary>
    /// <remarks>
    /// Quando existe <c>FinalError</c>, ele descreve algo que aconteceu FORA do executor
    /// (encerramento do Host, por exemplo) e continua vencendo — o adaptador não tem como saber
    /// disso. Sem esse erro externo, quem decide passa a ler o TIPO declarado pelo adaptador em
    /// vez de procurar sinal dentro do texto do nosso próprio código.
    /// </remarks>
    private static AgentRunOutcome ClassifyRun(AgentRunSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.FinalError)
            ? AgentRunOutcomeClassifier.Classify(
                snapshot.Execution!.Status,
                snapshot.Execution.FailureKind,
                snapshot.Execution.FailureCode,
                snapshot.Execution.FailureDiagnostic)
            : AgentRunOutcomeClassifier.Classify(
                snapshot.Execution!.Status,
                snapshot.FinalError,
                snapshot.Execution.FailureDiagnostic);

    /// <summary>
    /// Motivo gravado na tentativa. Quando a falha é da CONTA — cota esgotada, login exigido — o
    /// motivo canônico substitui o código cru do executor. Sem isso, o circuito do CARD recebia
    /// `executor.exit_code_1` e não tinha como saber que quem falhou foi o provedor: três runs
    /// perdidos por cota matavam um card perfeitamente saudável.
    /// </summary>
    private static string? ResolveAttemptFailureReason(
        AgentRunSnapshot snapshot, bool countsTowardRoundBudget)
    {
        if (snapshot.Status != AgentRunStatus.Failed)
        {
            return null;
        }

        if (snapshot.Execution is not null)
        {
            var outcome = ClassifyRun(snapshot);
            if (outcome.Kind is AgentRunOutcomeKind.QuotaExhausted
                or AgentRunOutcomeKind.AuthenticationRequired)
            {
                return outcome.ReasonCode;
            }
        }

        return snapshot.FinalError
            ?? snapshot.Execution?.FailureCode
            ?? (countsTowardRoundBudget ? "run.permanent_failure" : "run.failed");
    }

    /// <summary>
    /// Falha transitória não gasta rodada, mas também não autoriza retry em rajada. A mesma janela
    /// usada para uma largada recusada dá tempo para conta, rede ou processo se recuperar sem
    /// transformar cada ciclo do Chief em uma nova tentativa durável.
    /// </summary>
    public static TimeSpan? RetryDelayAfterRun(AgentRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Status is AgentRunStatus.Failed or AgentRunStatus.Cancelled
            ? FailedRunRetryBackoff
            : null;
    }

    /// <summary>Teto do freio: a partir daqui a fila respira em vez de bater de dois em dois minutos.</summary>
    public static readonly TimeSpan MaximumNoProgressBackoff = TimeSpan.FromMinutes(32);

    /// <summary>
    /// Freio para SEQUÊNCIA SEM PROGRESSO — a pergunta que o circuito do card não faz.
    ///
    /// O circuito responde "esta falha é culpa do card?" e responde certo: uma tentativa de zero
    /// token não julgou o enunciado, então não pode abrir circuito nem queimar rodada. A
    /// consequência não intencional é que o pior sintoma possível — a tentativa que não produz
    /// NADA — era justamente o único que não tinha nenhum freio. Medido em 03/08/2026: catorze
    /// tentativas idênticas em uma hora e quarenta, dois cards alternando, zero token em todas,
    /// nenhum circuito aberto, nenhum sinal de parada.
    ///
    /// A resposta certa não é culpar o card: é ESPAÇAR. O intervalo dobra a cada run consecutivo
    /// sem saída e satura; qualquer run que produza um token zera a contagem. Assim uma parede
    /// nova — a próxima, a que ninguém previu — custa minutos em vez de uma noite, e continua
    /// recuperável sozinha no instante em que ela sair da frente.
    /// </summary>
    public static TimeSpan NoProgressBackoff(int consecutiveNoProgressRuns)
    {
        if (consecutiveNoProgressRuns <= 1)
        {
            return FailedRunRetryBackoff;
        }

        // O expoente é limitado antes da multiplicação: um contador alto não pode estourar o
        // TimeSpan no caminho até o teto.
        var steps = Math.Min(consecutiveNoProgressRuns - 1, 8);
        var grown = FailedRunRetryBackoff * Math.Pow(2, steps);
        return grown > MaximumNoProgressBackoff ? MaximumNoProgressBackoff : grown;
    }

    /// <summary>
    /// Um run produziu saída? É o sinal de PROGRESSO — não de sucesso. Um run que falhou depois
    /// de escrever mil tokens andou; um que morreu sem um token não andou, e é a repetição
    /// DESSE que precisa de freio.
    /// </summary>
    private static bool ProducedOutput(AgentRunSnapshot snapshot) =>
        snapshot.Execution?.Usage is { } usage && usage.OutputTokens > 0;

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
        IModelInvocationStore invocations,
        IWorkflowDocumentTemplateStore templates,
        CancellationToken token)
    {
        var reviewed = 0;
        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, "review", null, null, "active", null, 0, 50),
            token);
        foreach (var task in page.Items)
        {
            token.ThrowIfCancellationRequested();
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

            // D1 — O PARECER DO CONSELHO NÃO É REVISTO, PORQUE ELE JÁ É A REVISÃO.
            //
            // Exigir revisão independente de um parecer é recursão: quem critica passa a precisar
            // de quem critique, e o custo não é filosófico, é de elenco. O parecer só pode ser
            // escrito por conta de papel `critic` (é quem tem o claim `docs/conselho/**`), então
            // CADA assento consome uma conta critic como ATOR e a revisão dele exige OUTRA. Com N
            // contas critic o conselho passa a exigir N≥2 e não paraleliza; medido em 03/08/2026,
            // com N=2 e uma delas sem cota, os seis assentos entregaram o parecer e escalaram com
            // `critic.none_available` — e a fase 4 não tinha como fechar, nem naquele dia nem nunca.
            //
            // O controle não se perde: quem protege o conselho é a CONSOLIDAÇÃO — um único veredito
            // bloqueante segura a fase, o piso de lentes distintas continua valendo e todo dissenso
            // fica no ledger. Um parecer ruim não passa por ser aprovado aqui; ele passa a valer
            // como opinião, e é a mesa inteira que decide.
            //
            // O veredito é gravado como review DETERMINÍSTICA — mesma via do gate de placeholder
            // logo abaixo —, com alias e motivo próprios. Não se inventa um segundo revisor: fica
            // registrado no ledger que a aprovação foi de POLÍTICA, e é auditável como tal.
            if (IsCouncilOpinionCard(task))
            {
                var councilResult = new CriticReviewResult(
                    UlidValue.New(now).ToString(), awaiting.Id, "deterministic-council-gate",
                    "deterministic", awaiting.AgentId, CriticVerdict.Pass,
                    "critic.pass",
                    [],
                    "Parecer do Conselho: a revisão independente deste card é a própria " +
                    "consolidação do conselho (um veredito bloqueante segura a fase). Revisar o " +
                    "parecer exigiria uma segunda conta de crítico por assento e tornaria o " +
                    "conselho impossível com o elenco disponível.",
                    null,
                    0);
                // A camada determinística é DECLARADA, não omitida. Nenhum gate determinístico
                // roda para um parecer — ele não tem branch de código a inspecionar —, e omitir
                // a camada agora bloqueia (ausência deixou de valer como aprovação). Registrar
                // aqui que quem a satisfez foi a POLÍTICA do conselho mantém o comportamento e
                // deixa a decisão auditável: o ledger diz por que passou, em vez de fingir que
                // alguém verificou.
                if (await ApplyReviewVerdictAsync(
                        tenantId, task, awaiting.Id, councilResult, chain, token,
                        new LayerResult(
                            VerificationLayer.Deterministic,
                            LayerVerdict.Pass,
                            "council.policy_accepted",
                            "parecer do Conselho: aprovação de política, sem gate determinístico aplicável")))
                {
                    reviewed++;
                    ClearReviewDeferrals(awaiting.Id);
                    LogCouncilOpinionAccepted(logger, task.Id, awaiting.Id);
                }
                else
                {
                    _ = await DeferOrEscalateReviewAsync(
                        tenantId, task, awaiting.Id, "council.gate_not_applied", chain, now, token);
                }

                continue;
            }

            var instructions = await board.ListInstructionsAsync(tenantId, task.Id, null, 50, token);
            if (instructions.Count == 0)
            {
                continue;
            }

            var resolution = ChiefCardResolver.Resolve(
                task.Title, instructions[^1].Body, [], task.Priority);
            // A tentativa pertence ao PROFISSIONAL (persona) e por isso `AgentId` é o id dele,
            // não o alias secreto/operacional da conta. O alias executor vem do ledger de
            // invocações da própria tentativa; é esse fato que impede o mesmo provider de revisar
            // o próprio trabalho.
            var taskInvocations = await invocations.GetTaskInvocationsAsync(
                tenantId, task.Id, token);
            var producerAlias = taskInvocations
                .Where(entry => string.Equals(
                    entry.AttemptId, awaiting.Id, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.InvokedAt)
                .Select(entry => entry.AccountAlias)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(producerAlias))
            {
                LogNoCriticAvailable(logger, task.Id, awaiting.AgentId);
                _ = await DeferOrEscalateReviewAsync(
                    tenantId, task, awaiting.Id, "critic.producer_alias_unknown", chain, now, token);
                continue;
            }
            var criticAliases = SelectCriticAliases(producerAlias, now);
            var criticAlias = criticAliases.Length == 0 ? null : criticAliases[0];

            // B7/F17 — revisão pareada como REGRA, não como disponibilidade. A política decide se
            // esta rodada pode seguir para submissão: em risco alto e crítico o revisor é
            // obrigatório, e revisor igual ao executor não conta como revisor — ele repete o
            // raciocínio que o trouxe até aqui e o encontra correto. Em risco baixo a ausência é
            // tolerada, e é a política que diz isso, não a sorte de haver conta livre.
            var pairing = ContinuousReviewPolicy.Evaluate(
                Enum.TryParse<RiskTier>(task.Priority, ignoreCase: true, out var taskRisk)
                    ? taskRisk
                    : RiskTier.Medium,
                producerAlias,
                criticAlias,
                []);

            if (criticAlias is null || !pairing.MaySubmit)
            {
                LogNoCriticAvailable(logger, task.Id, producerAlias);
                _ = await DeferOrEscalateReviewAsync(
                    tenantId, task, awaiting.Id, "critic.none_available", chain, now, token,
                    reviewerReturnsBy: CriticRosterReturnsBy(producerAlias, now));
                continue;
            }

            var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl!);
            var branch = $"task/agent-run-{awaiting.Id.ToLowerInvariant()}";
            string diff;
            CodeGraphBuildResult? branchInspection = null;
            DocumentTemplateValidationResult? documentTemplateValidation = null;

            // OPS-071 — quem executa o gate é a PLATAFORMA. O pacote de objetivo exige
            // "Gates: build, tests" e o revisor cobra a prova de execução, corretamente; mas a CLI
            // que executa o card roda em sandbox própria que nega rodar o runtime dentro da
            // worktree. Enquanto a prova fosse pedida a quem não pode executar, nenhum card de
            // código podia ser aprovado — dois revisores independentes já reprovaram por isso, com
            // achado P0, entregas que não tinham outro defeito.
            var requiredGates = DeliveryGateExecutionPolicy.ParseRequiredGates(instructions[^1].Body);
            var gateReport = new DeliveryGateReport(
                [], DeliveryGateExecutionPolicy.ReasonNotRequired, "o card não exige gate executável");
            try
            {
                using var manager = await GitWorktreeManager.OpenAsync(
                    repositoryRoot, controlledRoot, token);
                diff = await manager.DiffBranchAsync("HEAD", branch, token);

                // Documento inválido não deve consumir um crítico e só descobrir o defeito depois
                // de aprovado. O mesmo contrato é repetido na publicação como defesa em
                // profundidade; aqui a falha vira review determinístico e segue pelo fluxo normal
                // de correções, em vez de reaparecer em todo ciclo como publicação recusada.
                if (string.Equals(task.CardType, "documento", StringComparison.Ordinal))
                {
                    var artifacts = ApprovedDocumentCatalogPublisher.SelectDocumentArtifacts(
                        await manager.ListBranchChangedFilesAsync(branch, token));
                    if (artifacts.Count != 1)
                    {
                        documentTemplateValidation = new(
                            false,
                            artifacts.Count == 0
                                ? "document.artifact_missing"
                                : "document.artifact_ambiguous",
                            artifacts.Count == 0
                                ? "nenhum arquivo Markdown em docs/ foi entregue"
                                : $"{artifacts.Count} arquivos Markdown em docs/ foram entregues; o card documental exige exatamente um");
                    }
                    else
                    {
                        var body = await manager.ReadDocumentFromBranchAsync(
                            branch, artifacts[0], token);
                        documentTemplateValidation =
                            ApprovedDocumentCatalogPublisher.ValidateTemplateContract(
                                instructions[^1].Body,
                                body,
                                await templates.ListAsync(token));
                    }
                }

                // Gate determinístico PRÉ-REVIEW sobre a branch real. A branch pode já estar numa
                // worktree viva; se não estiver, criamos uma worktree efêmera governada e a
                // removemos sem apagar a branch. O índice transitório nunca substitui o grafo
                // publicado do projeto.
                var registered = await manager.ListWorktreesAsync(token);
                var existing = registered.FirstOrDefault(item =>
                    string.Equals(item.BranchName, branch, StringComparison.Ordinal));
                var inspectionPath = existing?.WorktreePath ??
                    System.IO.Path.Combine(controlledRoot, "code-graph-review", awaiting.Id);
                var ownsInspectionWorktree = existing is null;
                if (ownsInspectionWorktree)
                {
                    System.IO.Directory.CreateDirectory(
                        System.IO.Path.GetDirectoryName(inspectionPath)!);
                    _ = await manager.CreateTaskWorktreeAsync(
                        branch, awaiting.Id, inspectionPath, cancellationToken: token);
                }

                try
                {
                    branchInspection = await codeGraph.InspectAsync(
                        project.Id, inspectionPath, token);

                    // Os gates rodam AQUI, com a worktree da tentativa viva e antes de ela ser
                    // removida — é a única janela em que o que se executa é exatamente o que se
                    // revisa. O plano sai do manifesto entregue; o executor impõe ambiente mínimo,
                    // ausência de rede e teto de tempo.
                    if (requiredGates.Count > 0)
                    {
                        var plan = DeliveryGateExecutionPolicy.Plan(
                            requiredGates, DeliveryGateRunner.CollectDeclarations(inspectionPath));
                        var outcomes = await DeliveryGateRunner.RunAsync(plan, inspectionPath, token);
                        gateReport = DeliveryGateExecutionPolicy.Consolidate(plan, outcomes);
                        LogDeliveryGatesRan(
                            logger, task.Id, awaiting.Id, gateReport.ReasonCode, gateReport.Detail);
                    }
                }
                finally
                {
                    if (ownsInspectionWorktree)
                    {
                        _ = await manager.RemoveTaskWorktreeAsync(
                            branch, inspectionPath, deleteBranch: false, token);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogReviewInfrastructureFailure(
                    logger,
                    task.Id,
                    awaiting.Id,
                    $"review-preflight:{exception.GetType().Name}");
                _ = await DeferOrEscalateReviewAsync(
                    tenantId, task, awaiting.Id,
                    $"review-preflight:{exception.GetType().Name}", chain, now, token);
                continue;
            }

            if (documentTemplateValidation is { IsValid: false } documentFailure)
            {
                var detail = string.IsNullOrWhiteSpace(documentFailure.Detail)
                    ? documentFailure.ReasonCode
                    : documentFailure.Detail;
                var deterministicResult = new CriticReviewResult(
                    UlidValue.New(now).ToString(), awaiting.Id, "deterministic-document-gate",
                    "deterministic", producerAlias, CriticVerdict.Fail,
                    "critic.fail",
                    [new CriticFinding(
                        CriticFindingSeverity.P1,
                        documentFailure.ReasonCode,
                        detail,
                        null,
                        "Corrija a estrutura documental e submeta uma nova versão.")],
                    $"Gate documental recusou a entrega: {detail}",
                    null,
                    0)
                {
                    RejectionCause = ReviewRejectionCause.QualityBar,
                };
                // Quem reprovou foi o gate DOCUMENTAL, e é isso que precisa constar. Sem passar
                // o veredito real, a mesma entrega ficava registrada com a camada determinística
                // "limpa" — a auditoria leria o contrário do que houve.
                if (await ApplyReviewVerdictAsync(
                        tenantId, task, awaiting.Id, deterministicResult, chain, token,
                        new LayerResult(
                            VerificationLayer.Deterministic,
                            LayerVerdict.Fail,
                            documentFailure.ReasonCode)))
                {
                    reviewed++;
                    ClearReviewDeferrals(awaiting.Id);
                }
                else
                {
                    _ = await DeferOrEscalateReviewAsync(
                        tenantId, task, awaiting.Id, "document.gate_not_applied", chain, now, token);
                }

                continue;
            }

            var diagnosticVerdict = CodeDiagnosticsGate.Inspect(
                branchInspection!.Diagnostics, branchInspection.DiagnosticScope);
            var diagnosticLayer = CodeDiagnosticsGate.ApplyTo(
                new LayerResult(
                    VerificationLayer.Deterministic,
                    LayerVerdict.Pass,
                    CodeDiagnosticsGate.ReasonClean),
                diagnosticVerdict);
            if (!LayeredVerificationPolicy.MayOccupyReviewer([diagnosticLayer]))
            {
                if (await ApplyCodeDiagnosticsFailureAsync(
                        tenantId, task, awaiting.Id, diagnosticVerdict, chain, token))
                {
                    reviewed++;
                    ClearReviewDeferrals(awaiting.Id);
                }
                else
                {
                    _ = await DeferOrEscalateReviewAsync(
                        tenantId, task, awaiting.Id, "diagnostics.gate_not_applied", chain, now, token);
                }

                continue;
            }

            // VARREDURA DE SEGREDO — a metade da camada determinística que independe do stack.
            // Roda sobre o diff INTEIRO, antes do truncamento de 160 kB: um segredo que caísse
            // depois do corte passaria despercebido justamente na entrega mais longa. Vale para
            // documento também: um passo-a-passo com a chave colada dentro vaza igual.
            var secretVerdict = DeliverySecretScanGate.Inspect(diff);
            var scannedLayer = DeliverySecretScanGate.ApplyTo(diagnosticLayer, secretVerdict);

            // A camada só é rebaixada, nunca promovida: gate verde não compensa segredo achado.
            var deterministicLayer = DeliveryGateExecutionPolicy.ApplyTo(scannedLayer, gateReport);
            if (!secretVerdict.IsClean)
            {
                var secretResult = new CriticReviewResult(
                    UlidValue.New(now).ToString(), awaiting.Id, "deterministic-secret-gate",
                    "deterministic", producerAlias, CriticVerdict.Fail,
                    "critic.fail",
                    [.. secretVerdict.Findings.Select(finding => new CriticFinding(
                        CriticFindingSeverity.P1,
                        DeliverySecretScanGate.ReasonSecretFound,
                        $"Credencial no formato {finding.PatternName} foi adicionada em " +
                        $"{finding.File}. Remova o segredo do código e use referência a segredo; " +
                        "considere a credencial comprometida e faça a rotação.",
                        finding.File,
                        finding.Excerpt))],
                    "A entrega adiciona segredo em texto claro. O critério do portão de " +
                    "Desenvolvimento exige entrega sem segredo em código.",
                    null,
                    0)
                {
                    RejectionCause = ReviewRejectionCause.QualityBar,
                };
                if (await ApplyReviewVerdictAsync(
                        tenantId, task, awaiting.Id, secretResult, chain, token, deterministicLayer))
                {
                    reviewed++;
                    ClearReviewDeferrals(awaiting.Id);
                }
                else
                {
                    _ = await DeferOrEscalateReviewAsync(
                        tenantId, task, awaiting.Id, "secret.gate_not_applied", chain, now, token);
                }

                continue;
            }

            // Gate exigido que a plataforma executou e reprovou, ou que não pôde ser apurado. Não
            // ocupa revisor: build quebrado e teste vermelho são fatos, e a resposta deles já está
            // escrita. A exceção é falta de RUNTIME no host — essa é falha nossa, e punir o card
            // por ela seria a mesma misatribuição de culpa que esta operação já corrigiu três vezes.
            if (!gateReport.NotApplicable && !gateReport.AllPassed)
            {
                if (string.Equals(
                        gateReport.ReasonCode,
                        DeliveryGateExecutionPolicy.ReasonRuntimeUnavailable,
                        StringComparison.Ordinal))
                {
                    LogReviewInfrastructureFailure(
                        logger, task.Id, awaiting.Id, gateReport.ReasonCode);
                    _ = await DeferOrEscalateReviewAsync(
                        tenantId, task, awaiting.Id, gateReport.ReasonCode, chain, now, token);
                    continue;
                }

                var gateResult = new CriticReviewResult(
                    UlidValue.New(now).ToString(), awaiting.Id, "deterministic-delivery-gates",
                    "deterministic", producerAlias, CriticVerdict.Fail,
                    DeterministicRejectionReasonCode,
                    [new CriticFinding(
                        CriticFindingSeverity.P1,
                        gateReport.ReasonCode,
                        gateReport.Detail,
                        null,
                        DeliveryGateExecutionPolicy.DescribeForReviewer(gateReport))],
                    "A plataforma executou os gates declarados pela própria entrega e o " +
                    $"resultado não autoriza a revisão: {gateReport.Detail}",
                    null,
                    0)
                {
                    RejectionCause = ReviewRejectionCause.AcceptanceNotMet,
                };
                if (await ApplyReviewVerdictAsync(
                        tenantId, task, awaiting.Id, gateResult, chain, token, deterministicLayer))
                {
                    reviewed++;
                    ClearReviewDeferrals(awaiting.Id);
                }
                else
                {
                    _ = await DeferOrEscalateReviewAsync(
                        tenantId, task, awaiting.Id, "delivery_gates.not_applied", chain, now, token);
                }

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

            var placeholders = ForbiddenDeliveryPlaceholders(diff);
            if (placeholders.Count > 0)
            {
                var deterministicResult = new CriticReviewResult(
                    UlidValue.New(now).ToString(), awaiting.Id, "deterministic-delivery-gate",
                    "deterministic", producerAlias, CriticVerdict.Fail,
                    // REPROVAÇÃO REAL, não falha de infraestrutura. O código precisa estar em
                    // `AppliableReviewReasons`, senão `ApplyReviewVerdictAsync` devolve `false`
                    // por definição: o veredito nunca é aplicado, o card adia quatro vezes e
                    // escala como "revisão indisponível" — culpando o revisor por algo que o
                    // gate determinístico apurou sozinho e sabia dizer. Os gates irmãos
                    // (documental e de segredo) já usam `critic.fail`; este era o único fora.
                    DeterministicRejectionReasonCode,
                    [.. placeholders.Select(value => new CriticFinding(
                        CriticFindingSeverity.P1,
                        "delivery.placeholder",
                        "A entrega contém placeholder não resolvido.",
                        null,
                        value))],
                    "Placeholders de entrega precisam ser resolvidos antes da revisão comportamental.",
                    null,
                    0)
                {
                    RejectionCause = ReviewRejectionCause.QualityBar,
                };
                if (await ApplyReviewVerdictAsync(
                        tenantId, task, awaiting.Id, deterministicResult, chain, token,
                        new LayerResult(
                            VerificationLayer.Deterministic,
                            LayerVerdict.Fail,
                            "delivery.placeholder",
                            string.Join("; ", placeholders))))
                {
                    reviewed++;
                    ClearReviewDeferrals(awaiting.Id);
                }
                else
                {
                    _ = await DeferOrEscalateReviewAsync(
                        tenantId, task, awaiting.Id, "delivery.gate_not_applied", chain, now, token);
                }

                continue;
            }

            // O review roda dentro do ciclo; um executor de crítico que TRAVE congelaria o loop
            // inteiro (colheita, correções, triagem e despacho). O teto local garante que o
            // ciclo sempre volta: estouro vira falha de infraestrutura com backoff, nunca
            // reprovação do ator.
            CriticReviewResult? result = null;
            foreach (var candidateAlias in criticAliases)
            {
                using var reviewTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                reviewTimeout.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    result = await orchestrator.ReviewAsync(
                        new AgentCriticReviewCommand
                        {
                            AttemptId = awaiting.Id,
                            TenantId = tenantId,
                            ProjectId = project.Id,
                            TaskId = task.Id,
                            CriticAlias = candidateAlias,
                            ActorAlias = producerAlias,
                            ReviewDirectory = repositoryRoot,
                            Diff = diff,
                            DelegationInstruction = instructions[^1].Body,
                            // A evidência de execução vem de QUEM EXECUTOU. Antes eram só as
                            // referências duráveis da tentativa, e o revisor — corretamente —
                            // lia isso como "nenhuma prova de que os gates rodaram" e reprovava
                            // com P0. O ator não podia produzir essa prova: a sandbox da CLI
                            // dele nega rodar o runtime. Agora quem produz é a plataforma, e o
                            // resultado real chega aqui.
                            TestEvidence = ComposeTestEvidence(awaiting.CommitRefs, gateReport),
                            AcceptanceCriteria = resolution.Card.AcceptanceCriteria,
                            ScopeClaims = resolution.ScopeClaims,
                        },
                        reviewTimeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    LogReviewInfrastructureFailure(logger, task.Id, awaiting.Id, "critic.review_timeout");
                    result = null;
                }

                if (result is not null && AppliableReviewReasons.Contains(result.ReasonCode))
                {
                    break;
                }

                if (result is not null)
                {
                    LogReviewInfrastructureFailure(
                        logger, task.Id, awaiting.Id, result.ReasonCode);
                }
            }

            if (result is not null &&
                await ApplyReviewVerdictAsync(
                    tenantId, task, awaiting.Id, result, chain, token, deterministicLayer))
            {
                reviewed++;
                ClearReviewDeferrals(awaiting.Id);
            }
            else
            {
                _ = await DeferOrEscalateReviewAsync(
                    tenantId, task, awaiting.Id,
                    result?.ReasonCode ?? "critic.executor_unavailable", chain, now, token);
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

    /// <param name="deterministicVerdict">
    /// O que os gates DETERMINÍSTICOS realmente disseram sobre esta entrega.
    ///
    /// Era uma constante `Pass` com motivo `clean`, e isso produzia um registro falso: quando o
    /// gate documental reprovava, o veredito sintético entrava por aqui e o ledger gravava
    /// "diagnóstico de código limpo" para uma entrega que acabara de falhar num gate
    /// determinístico. Quem auditasse depois lia o oposto do que aconteceu — e rastreabilidade
    /// que mente é pior que rastreabilidade ausente, porque ninguém desconfia dela.
    ///
    /// Omitir passou a BLOQUEAR (`NotRun`), não a aprovar: todo chamador precisa declarar o que
    /// os gates observaram. Quando nenhum gate se aplica — o parecer do Conselho é o caso —, a
    /// declaração é explícita e nomeia a política que aprovou.
    /// </param>
    internal async Task<bool> ApplyReviewVerdictAsync(
        string tenantId,
        BoardTaskRecord task,
        string attemptId,
        CriticReviewResult result,
        IWorkChainStore chain,
        CancellationToken token,
        LayerResult? deterministicVerdict = null)
    {
        if (!AppliableReviewReasons.Contains(result.ReasonCode))
        {
            LogReviewInfrastructureFailure(logger, task.Id, attemptId, result.ReasonCode);
            return false;
        }

        // B2/F14 — o veredito final é COMPOSTO pelas camadas, e camada superior não compensa
        // inferior. O que cada uma registra precisa ser o que ela de fato observou.
        //
        // A determinística vem de QUEM A EXECUTOU: os gates documental e de diagnóstico de código
        // rodam antes desta chamada, e o resultado deles é passado adiante. Assumi-la aprovada
        // aqui gravava "limpo" até para a entrega que tinha acabado de ser reprovada por eles.
        //
        // Comportamento e intenção continuam sendo duas perguntas distintas ao crítico — mas
        // quando ele devolve um veredito único, declarar duas camadas idênticas seria inventar
        // uma segunda opinião que ninguém deu. Uma resposta, uma camada: o que sustenta a decisão
        // é o critério de aceite, e é ele que fica no registro.
        var criticVerdict = result.Approved ? LayerVerdict.Pass : LayerVerdict.Fail;
        var layers = new[]
        {
            // AUSÊNCIA NÃO É APROVAÇÃO. O default era `Pass`/`clean`: quem chamasse sem passar o
            // veredito ganhava uma camada determinística limpa que ninguém executou — e a própria
            // política diz, no enum, que camada não executada "NUNCA conta como aprovação". Enquanto
            // todo card era de documento o gate documental cobria o buraco; um card de CÓDIGO seria
            // aprovado com "camada determinística limpa" sem nada ter sido compilado, testado ou
            // varrido. Agora quem não declara recebe `NotRun`, e `Evaluate` bloqueia.
            deterministicVerdict ?? LayeredVerificationPolicy.NotDeclared(
                VerificationLayer.Deterministic,
                "nenhum gate determinístico declarou veredito para esta entrega"),
            new LayerResult(VerificationLayer.Behavioral, criticVerdict, result.ReasonCode),
            new LayerResult(VerificationLayer.Intent, criticVerdict, result.ReasonCode),
        };
        var layered = LayeredVerificationPolicy.Evaluate(layers);

        var decision = layered.Approved ? "approved" : "rejected";
        var findingsSummary = result.Findings.Count == 0
            ? string.Empty
            : $" Achados: {string.Join("; ", result.Findings.Select(finding => $"[{finding.Severity}] {finding.Summary}"))}";

        // Fase 1C: uma reprovação precisa NOMEAR a camada e a severidade. "Reprovado" sozinho não
        // diz a quem corrige o que consertar — build quebrado, critério de aceite não atendido e
        // objetivo da demanda não cumprido exigem ações completamente diferentes, e quem lê o
        // parecer precisa saber qual delas é a sua.
        var layerVerdict = layered.Approved || layered.BlockedAt is not { } blocked
            ? string.Empty
            : $"[camada {(int)blocked}/{blocked} · severidade {layered.Severity}] ";
        var rationale = $"{layerVerdict}{result.Summary ?? result.ReasonCode}{findingsSummary}";
        if (rationale.Length > 4_000)
        {
            rationale = rationale[..4_000];
        }

        var applied = await chain.ReviewAttemptAsync(
            new WorkAttemptReviewCommand(
                tenantId, task.BackingSolicitationId, task.Id, attemptId, result.ReviewId,
                result.CriticAlias, decision, rationale, task.Version,
                $"chief-loop-review:{result.ReviewId}", clock.UtcNow)
            {
                RejectionCause = ToStorageRejectionCause(result.RejectionCause),
            },
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

    private static string ToStorageRejectionCause(ReviewRejectionCause cause) =>
        cause switch
        {
            ReviewRejectionCause.None => "none",
            ReviewRejectionCause.ContextMissing => "contextMissing",
            ReviewRejectionCause.AcceptanceNotMet => "acceptanceNotMet",
            ReviewRejectionCause.ScopeViolation => "scopeViolation",
            ReviewRejectionCause.QualityBar => "qualityBar",
            ReviewRejectionCause.Other => "other",
            _ => "other",
        };

    /// <summary>
    /// A "Evidência de testes" que o revisor lê. Duas fontes, e a ordem importa: primeiro o que a
    /// PLATAFORMA executou (fato), depois as referências duráveis da tentativa (rastro).
    ///
    /// Sem a primeira, o revisor lia apenas commits e — corretamente — concluía que não havia prova
    /// de execução; era o achado P0 que reprovava toda entrega de código, incluindo as que não
    /// tinham outro defeito. O ator não podia produzir essa prova: a sandbox da CLI dele nega rodar
    /// o runtime na worktree. Continuar exigindo dele seria manter um gate que ninguém pode passar.
    /// </summary>
    internal static string ComposeTestEvidence(
        IReadOnlyList<string> commitRefs, DeliveryGateReport gateReport)
    {
        ArgumentNullException.ThrowIfNull(commitRefs);
        ArgumentNullException.ThrowIfNull(gateReport);

        var references = commitRefs.Count == 0
            ? "(nenhuma referência durável foi registrada)"
            : string.Join(Environment.NewLine, commitRefs.Select(reference => $"- {reference}"));

        if (gateReport.NotApplicable)
        {
            return commitRefs.Count == 0
                ? "(nenhuma evidência durável foi registrada; falhe fechado)"
                : references;
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            DeliveryGateExecutionPolicy.DescribeForReviewer(gateReport),
            "Referências duráveis da tentativa:",
            references);
    }

    private async Task<bool> ApplyCodeDiagnosticsFailureAsync(
        string tenantId,
        BoardTaskRecord task,
        string attemptId,
        CodeDiagnosticsVerdict verdict,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var reviewId = UlidValue.New(clock.UtcNow).ToString();
        var applied = await chain.ReviewAttemptAsync(
            new WorkAttemptReviewCommand(
                tenantId,
                task.BackingSolicitationId,
                task.Id,
                attemptId,
                reviewId,
                "code-diagnostics-gate",
                "rejected",
                $"{verdict.ReasonCode}: {verdict.Detail}",
                task.Version,
                $"chief-loop-code-diagnostics:{attemptId}",
                clock.UtcNow)
            {
                RejectionCause = ToStorageRejectionCause(ReviewRejectionCause.QualityBar),
            },
            token);
        if (applied.Status is WorkChainMutationStatus.Applied
            or WorkChainMutationStatus.IdempotentReplay)
        {
            LogReviewApplied(
                logger, task.Id, attemptId, "code-diagnostics-gate", "rejected");
            return true;
        }

        LogReviewInfrastructureFailure(
            logger, task.Id, attemptId, $"chain:{applied.Status}");
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
            token.ThrowIfCancellationRequested();
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
    /// <summary>
    /// Elo de COMUNICAÇÃO PROATIVA: quando uma etapa do playbook fecha, a Diretora de Engenharia
    /// conta ao dono o que ficou pronto e o que vem a seguir.
    ///
    /// Existe porque a esteira andava em silêncio: entre a pergunta inicial e a entrega final o
    /// projeto atravessava etapas inteiras sem uma palavra, e quem saía do computador não tinha
    /// como saber se o trabalho seguia, tinha parado ou estava esperando por ele. Anunciar
    /// escalação cobria só o caminho ruim; o bom não existia.
    ///
    /// A linguagem é de negócio: etapa, entrega, próximo passo. Nada de card, gate, objetivo,
    /// estado interno ou identificador.
    /// </summary>
    /// <summary>Abertura fixa do aviso de parada. É por ela que um aviso já dito é reconhecido.</summary>
    private const string StallMarker = "Uma pausa no andamento:";

    /// <summary>Abertura fixa do aviso de retomada.</summary>
    private const string ResumedMarker = "Voltamos a andar:";

    /// <summary>Projetos cujo aviso de parada já foi dito nesta execução do processo.</summary>
    private readonly HashSet<string> _stallAnnounced = new(StringComparer.Ordinal);

    /// <summary>Quando este processo subiu. Base da carência que evita alarme causado por deploy.</summary>
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <summary>Silêncio esperado logo após a subida — o que estava em voo foi cancelado por nós.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A Bruna avisa quando a esteira PARA — e avisa de novo quando ela volta.
    ///
    /// Existe por uma pergunta do proprietário que o produto não sabia responder: "se algo
    /// tivesse parado, eu só ia perceber horas depois?". A resposta era sim. Em 2026-08-03 a
    /// fábrica passou uma hora e meia sem produzir nada e ninguém foi avisado: o quadro mostrava
    /// exatamente o que mostraria se estivesse tudo bem, e o único aviso que existia — o do
    /// marco de etapa — só dispara quando algo FECHA. Ausência de notícia era indistinguível
    /// de trabalho em curso.
    ///
    /// Duas disciplinas evitam que isto vire ruído. Só se fala na TRANSIÇÃO (andando → parado,
    /// parado → andando), nunca a cada ciclo; e o que já foi dito é reconhecido na própria
    /// conversa, para que um reinício do Host não repita o mesmo aviso sem fato novo.
    /// </summary>
    private async Task AnnounceDeliveryStallAsync(
        string tenantId,
        ProjectRecord project,
        IWorkBoardStore board,
        IServiceScope scope,
        CancellationToken token)
    {
        var now = clock.UtcNow;
        var health = await ReadDeliveryHealthAsync(tenantId, project.Id, board, now, token);
        if (health is null)
        {
            return;
        }

        var key = project.Id;
        var stalledNow = health.Value.Stalled;
        var announced = _stallAnnounced.Contains(key);
        if (stalledNow == announced)
        {
            return;
        }

        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var open = await conversations.ListConversationsAsync(tenantId, project.Id, null, 1, token);
        if (open.Count == 0)
        {
            return;
        }

        var content = stalledNow
            ? $"{StallMarker} o trabalho desta etapa está sem avançar há cerca de " +
              $"{health.Value.IdleMinutes} minutos. {health.Value.Explanation} " +
              "Estou acompanhando e retomo assim que o caminho abrir — você não precisa fazer " +
              "nada. Se eu precisar de uma decisão sua, eu te procuro."
            : $"{ResumedMarker} o trabalho desta etapa voltou a avançar. " +
              "Sigo daqui e te aviso quando a etapa fechar.";

        var result = await conversations.CreateMessageAsync(
            new MessageCreateCommand(
                tenantId,
                new MessageRecord(
                    tenantId, project.Id, UlidValue.New(now).ToString(), open[0].Id,
                    "chief", null, project.ChiefAgentId, content, null, now),
                now),
            token);

        if (result.Status != MessageMutationStatus.Applied)
        {
            return;
        }

        if (stalledNow)
        {
            _ = _stallAnnounced.Add(key);
        }
        else
        {
            _ = _stallAnnounced.Remove(key);
        }

        LogDeliveryStallAnnounced(logger, project.Id, stalledNow, health.Value.IdleMinutes);
    }

    /// <summary>
    /// Saúde da esteira DESTE projeto: há trabalho esperando e há quanto tempo nada é entregue.
    ///
    /// O sinal honesto não é "existe tarefa" nem "existe tentativa" — é a última tentativa que
    /// PRODUZIU. Fila com trabalho e nenhuma entrega recente é parede.
    /// </summary>
    private async Task<(bool Stalled, int IdleMinutes, string Explanation)?> ReadDeliveryHealthAsync(
        string tenantId,
        string projectId,
        IWorkBoardStore board,
        DateTimeOffset now,
        CancellationToken token)
    {
        var page = await PagedScan.CollectAsync<BoardTaskRecord>(
            async (offset, size) =>
            {
                var current = await board.PageTasksAsync(
                    tenantId,
                    new BoardTaskPageQuery(projectId, null, null, null, null, null, "active", null, offset, size),
                    token);
                return (current.Items, current.Total);
            },
            token);

        var waiting = page.Items.Count(task =>
            task.InternalState is "ready" or "running");
        if (waiting == 0)
        {
            return null;
        }

        DateTimeOffset? lastDelivery = null;
        string? lastWall = null;
        foreach (var task in page.Items)
        {
            var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 20, token);
            foreach (var attempt in attempts)
            {
                if (attempt.TokensOutput > 0 &&
                    (lastDelivery is null || attempt.StartedAt > lastDelivery))
                {
                    lastDelivery = attempt.StartedAt;
                }

                if (attempt.TokensOutput == 0 &&
                    !string.IsNullOrWhiteSpace(attempt.FailureReason) &&
                    !CardCircuitBreakerService.IsInfrastructureFailure(attempt.FailureReason))
                {
                    continue;
                }

                if (attempt.TokensOutput == 0 && !string.IsNullOrWhiteSpace(attempt.FailureReason))
                {
                    lastWall = attempt.FailureReason;
                }
            }
        }

        // Sem NENHUMA entrega registrada não existe linha de base, e sem linha de base não
        // existe "parou" — existe "ainda não começou". Anunciar aqui produziu, na primeira vez
        // que a regra rodou de verdade, a frase "sem avançar há cerca de 0 minutos" na conversa
        // do dono: o int.MaxValue virava zero na hora de escrever. Número sem sentido na fala da
        // diretora custa mais confiança que o silêncio que ele tentava corrigir.
        if (lastDelivery is not { } delivered)
        {
            return null;
        }

        var idle = (int)Math.Round((now - delivered).TotalMinutes);

        // O limiar é APRENDIDO, não decretado.
        //
        // Um número fixo erra dos dois lados: curto demais e a Bruna avisa "parou" enquanto o
        // trabalho corre normal — e aviso falso é pior que silêncio, porque ensina o dono a
        // ignorar o canal; longo demais e a parede fica invisível pelo tempo que já custou uma
        // hora e meia nesta operação. O que o histórico deste projeto diz é quanto uma entrega
        // REALMENTE leva, e é contra isso que a espera deve ser medida.
        var expected = await ReadTypicalDeliveryMinutesAsync(tenantId, projectId, board, token);
        var threshold = Math.Clamp(expected * 3, settings.DeliveryStallMinutes, 45);

        // VIVACIDADE VETA A PARADA.
        //
        // "Sem entrega há 27 minutos" e "parado" não são a mesma coisa quando alguém está
        // trabalhando neste instante: uma tarefa pesada roda mais que a mediana sem que nada
        // esteja errado. Sem este veto, o aviso dispara no meio de um trabalho saudável — e um
        // vigia que grita à toa ensina o dono a ignorá-lo, que é o único jeito de ele falhar
        // quando estiver certo. Medido em 2026-08-03: a primeira vez que a regra disparou de
        // verdade, foi contra um card em plena execução.
        var working = page.Items.Any(task =>
            string.Equals(task.InternalState, "running", StringComparison.Ordinal));
        // Carência após a subida do processo: reiniciar o Host cancela o que estava em voo e
        // zera a atividade por alguns minutos. Sem esta carência, todo deploy anuncia ao dono
        // uma pausa que o próprio deploy causou — foi o que aconteceu às 17:37 de 2026-08-03.
        var sinceStart = now - _startedAt;
        if (sinceStart < StartupGrace)
        {
            return (false, idle, string.Empty);
        }

        var stalled = !working && idle >= threshold;
        var explanation = DescribeWallForOwner(lastWall);
        return (stalled, idle, explanation);
    }

    /// <summary>
    /// Quanto uma entrega deste projeto costuma levar, medido do próprio histórico.
    ///
    /// Mediana, não média: uma única tentativa órfã que ficou horas aberta distorce a média e
    /// esconderia exatamente a parede que se quer enxergar. Sem histórico suficiente devolve um
    /// valor conservador — é melhor demorar a avisar do que inventar uma expectativa a partir de
    /// duas amostras.
    /// </summary>
    private static async Task<int> ReadTypicalDeliveryMinutesAsync(
        string tenantId,
        string projectId,
        IWorkBoardStore board,
        CancellationToken token)
    {
        const int conservativeDefault = 10;
        var page = await PagedScan.CollectAsync<BoardTaskRecord>(
            async (offset, size) =>
            {
                var current = await board.PageTasksAsync(
                    tenantId,
                    new BoardTaskPageQuery(projectId, null, null, null, null, null, "all", null, offset, size),
                    token);
                return (current.Items, current.Total);
            },
            token);

        var durations = new List<double>();
        foreach (var task in page.Items)
        {
            var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 20, token);
            durations.AddRange(attempts
                .Where(attempt => attempt.TokensOutput > 0 && attempt.DurationMs is > 0)
                .Select(attempt => attempt.DurationMs!.Value / 60000d));
        }

        if (durations.Count < 5)
        {
            return conservativeDefault;
        }

        durations.Sort();
        var median = durations[durations.Count / 2];
        return Math.Max(1, (int)Math.Ceiling(median));
    }

    /// <summary>
    /// Traduz a parede para linguagem de NEGÓCIO. A Bruna não fala vocabulário técnico — e a
    /// política de comunicação recusa a mensagem inteira se ela escorregar, o que transformaria
    /// um aviso útil numa falha genérica na tela do dono.
    /// </summary>
    private static string DescribeWallForOwner(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Ainda estou apurando o motivo.";
        }

        // F-06: evitar Contains("quota")/Contains("authentication") — substring casa com texto
        // do próprio código ou de mensagens de erro do provedor. Os reason codes canônicos são
        // prefixados por conta/executor; comparar por igualdade remove a ambiguidade.
        if (string.Equals(reason, "account.quota_limited", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "executor.quota_exhausted", StringComparison.OrdinalIgnoreCase))
        {
            return "O serviço que faz esse trabalho atingiu o limite da janela dele e volta " +
                   "sozinho quando a janela renovar.";
        }

        if (string.Equals(reason, "account.authentication_required", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "executor.authentication_required", StringComparison.OrdinalIgnoreCase))
        {
            return "O acesso de um dos profissionais precisa ser renovado — isso depende de " +
                   "você, e eu te procuro para resolvermos.";
        }

        return "Estou tratando um problema de infraestrutura que não é do trabalho em si.";
    }

    private async Task<int> AnnouncePhaseMilestonesAsync(
        string tenantId,
        ProjectRecord project,
        IServiceScope scope,
        CancellationToken token)
    {
        var catalog = scope.ServiceProvider.GetRequiredService<IWorkflowCatalogStore>();
        var bindings = await catalog.ListBindingsAsync(tenantId, project.Id, null, 1, token);
        if (bindings.Count == 0)
        {
            return 0;
        }

        var runs = await catalog.ListRunsAsync(tenantId, bindings[0].Id, null, 20, token);
        var running = runs.FirstOrDefault(run =>
            string.Equals(run.State, "running", StringComparison.Ordinal));
        if (running is null)
        {
            return 0;
        }

        var aggregate = await scope.ServiceProvider.GetRequiredService<IWorkflowStore>()
            .ReadRunAggregateAsync(tenantId, running.Id, token);
        if (aggregate is null)
        {
            return 0;
        }

        var ordered = aggregate.Phases.OrderBy(phase => phase.Order).ToArray();
        var pending = ordered
            .Where(phase => string.Equals(phase.State, "completed", StringComparison.Ordinal))
            .Where(phase => _announcedMilestones.Add($"{project.Id}:{phase.PhaseRunId}"))
            .ToArray();
        if (pending.Length == 0)
        {
            return 0;
        }

        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var open = await conversations.ListConversationsAsync(tenantId, project.Id, null, 1, token);
        if (open.Count == 0)
        {
            // Sem conversa não há a quem contar. Libera para anunciar quando existir canal.
            foreach (var phase in pending)
            {
                _ = _announcedMilestones.Remove($"{project.Id}:{phase.PhaseRunId}");
            }

            return 0;
        }

        // A memória do processo não basta: cada reinício do Host repetiria o MESMO aviso, sem
        // nenhum fato novo. O registro durável do que já foi dito é a própria conversa.
        var history = await conversations.ListMessagesAsync(tenantId, open[0].Id, null, 200, token);
        var alreadySaid = history
            .Select(entry => entry.Content)
            .Where(content => content.Contains(MilestoneMarker, StringComparison.Ordinal))
            .ToArray();

        var now = clock.UtcNow;
        var announced = 0;
        foreach (var phase in pending)
        {
            token.ThrowIfCancellationRequested();
            var heading = MilestoneHeading(phase.Name);
            if (MilestoneAlreadyAnnounced(alreadySaid, phase.Name))
            {
                continue;
            }

            // O que o dono recebeu são os DOCUMENTOS da etapa, não os critérios do portão: o
            // objetivo de portão tem nome de checklist interno e não é entrega. Documento entregue
            // chega a `validated`; exigir `approved` esconderia exatamente o que ficou pronto.
            var delivered = phase.Objectives
                .Where(objective => string.Equals(
                    objective.Kind, "document", StringComparison.OrdinalIgnoreCase))
                .Where(objective => objective.State is "validated" or "approved")
                .Select(objective => $"- {objective.Name}")
                .ToArray();
            var next = ordered.FirstOrDefault(candidate => candidate.Order > phase.Order);

            var content =
                $"{heading} ✅\n\n" +
                (delivered.Length == 0
                    ? "A etapa fechou com as verificações exigidas para ela.\n\n"
                    : $"O que ficou pronto:\n{string.Join("\n", delivered)}\n\n") +
                (next is null
                    ? "Era a última etapa prevista. Vou consolidar o encerramento e te aviso.\n\n"
                    : $"Sigo agora para **{next.Name}**. Assim que ela fechar, te aviso de novo.\n\n") +
                "Você não precisa fazer nada neste momento — se eu precisar de uma decisão sua, " +
                "eu te procuro.";

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
                LogPhaseMilestoneAnnounced(logger, project.Id, phase.Name);
            }
            else
            {
                _ = _announcedMilestones.Remove($"{project.Id}:{phase.PhaseRunId}");
            }
        }

        return announced;
    }

    private async Task<int> AnnounceEscalatedCardsAsync(
        string tenantId,
        ProjectRecord project,
        string controlledRoot,
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
            token.ThrowIfCancellationRequested();
            if (await TryReplanEscalatedAsync(
                tenantId, project, task, controlledRoot, board,
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
                token.ThrowIfCancellationRequested();
                foreach (var task in escalated)
                {
                    // Reconhecimento pelo TÍTULO, não pelo identificador: o título é o que o dono
                    // lê, e publicar um id interno no chat violaria a projeção de negócio. O
                    // título do trabalho é estável e único dentro do projeto.
                    if (previous.Content.Contains(task.Title, StringComparison.Ordinal))
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
            token.ThrowIfCancellationRequested();
            var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
            // Reprovação de review tem state 'rejected' — contar 'failed' fazia a mensagem
            // dizer "reprovou 0 vezes" sobre um card reprovado duas (OPS-025, prova limpa).
            var rejected = attempts.Count(attempt =>
                string.Equals(attempt.State, "rejected", StringComparison.Ordinal));
            // Os achados valem da tentativa que os PRODUZIU, não da última: quando a mais
            // recente morreu por infraestrutura (cancelamento, cota), a nota do review fica
            // na anterior — e sem ela o dono era chamado a decidir às cegas.
            string? findings = null;
            foreach (var attempt in attempts.Reverse().Take(5))
            {
                var events = await board.ListAttemptEventsAsync(tenantId, attempt.Id, null, 50, token);
                findings = events.LastOrDefault(entry =>
                    string.Equals(entry.Kind, "note", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(entry.Content))?.Content;
                if (findings is not null)
                {
                    break;
                }
            }

            // Sem achado estruturado, o motivo canônico da escalação (blocked_reason) ainda é
            // melhor que o placeholder vazio: ele diz POR QUE o card parou.
            findings ??= string.IsNullOrWhiteSpace(task.BlockedReason)
                ? "(o review não registrou achados estruturados)"
                : task.BlockedReason;

            var content =
                $"{EscalationMarker} **{task.Title}**.\n\n" +
                $"A revisão independente reprovou este trabalho {rejected} vez(es) e ele chegou ao " +
                "limite de rodadas de correção. Continuar tentando do mesmo jeito só repetiria o " +
                "mesmo resultado, então parei e trouxe para você.\n\n" +
                $"O que a revisão apontou:\n{findings}\n\n" +
                "Me diga como prefere seguir: ajustar o que consideramos pronto, reduzir o que " +
                "essa parte precisa entregar, ou tratar isso como uma decisão do projeto.";

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
        string actorProfileId,
        ProjectRecord project,
        string controlledRoot,
        IWorkBoardStore board,
        IServiceScope scope,
        CancellationToken token)
    {
        var integration = scope.ServiceProvider.GetService<WorkBoard.TaskIntegrationService>();
        if (integration is null)
        {
            return 0;
        }
        var documentPublisher = scope.ServiceProvider
            .GetRequiredService<ApprovedDocumentCatalogPublisher>();

        var page = await board.PageTasksAsync(
            tenantId,
            new BoardTaskPageQuery(project.Id, null, null, null, null, null, "active", null, 0, 50),
            token);
        var integrated = 0;
        foreach (var task in page.Items)
        {
            token.ThrowIfCancellationRequested();
            // Convergência de versões anteriores: o card já foi revisado e integrado, mas o
            // catálogo permaneceu "em elaboração". O ID do documento é o ID do card por contrato.
            if (string.Equals(task.CardType, "documento", StringComparison.Ordinal) &&
                string.Equals(task.InternalState, "completed", StringComparison.Ordinal))
            {
                _ = await documentPublisher.EnsureReviewedDocumentApprovedAsync(
                    tenantId, task, task.Id, token);
                continue;
            }

            if (!string.Equals(task.InternalState, "approved", StringComparison.Ordinal))
            {
                continue;
            }

            SupersededDocumentProof? supersededDocument = null;
            if (string.Equals(task.CardType, "documento", StringComparison.Ordinal))
            {
                try
                {
                    var publication = await documentPublisher.PublishAsync(
                        tenantId, project, task, board, controlledRoot, token);
                    if (!publication.Published)
                    {
                        LogDocumentPublicationRefused(
                            logger,
                            task.Id,
                            string.IsNullOrWhiteSpace(publication.Detail)
                                ? publication.ReasonCode
                                : $"{publication.ReasonCode}: {publication.Detail}");
                        _ = await EnsureDocumentPublicationCorrectionAsync(
                            tenantId,
                            actorProfileId,
                            project,
                            task,
                            publication,
                            board,
                            token);
                        continue;
                    }

                    LogDocumentPublished(
                        logger,
                        task.Id,
                        publication.DocumentId ?? "-",
                        publication.DocumentVersionId ?? "-");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogDocumentPublicationRefused(
                        logger, task.Id, $"document.publish:{exception.GetType().Name}");
                    continue;
                }
            }
            else
            {
                try
                {
                    supersededDocument = await documentPublisher.ProveSupersededLegacyDocumentAsync(
                        tenantId, project, task, board, controlledRoot, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogDocumentPublicationRefused(
                        logger, task.Id, $"document.supersession-proof:{exception.GetType().Name}");
                    continue;
                }
            }

            var outcome = supersededDocument is null
                ? await integration.IntegrateAsync(
                    tenantId, task.Id, ChiefIntegrationActor, token)
                : await integration.IntegrateSupersededDocumentAsync(
                    tenantId,
                    task.Id,
                    ChiefIntegrationActor,
                    supersededDocument.EvidenceReference,
                    token);
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

    /// <summary>
    /// Recupera documentos aprovados por versões antigas que só descobriam o defeito estrutural
    /// na publicação. O parecer original continua imutável; nasce um card de atualização ligado
    /// à tentativa e ao artefato recusados, e o card anterior é encerrado como cancelado — nunca
    /// como entregue. A partir da versão atual o pré-review evita esse caminho, mas a recuperação
    /// é necessária para estado durável já existente e para drift defensivo entre gates.
    /// </summary>
    private async Task<bool> EnsureDocumentPublicationCorrectionAsync(
        string tenantId,
        string actorProfileId,
        ProjectRecord project,
        BoardTaskRecord task,
        ApprovedDocumentPublishResult publication,
        IWorkBoardStore board,
        CancellationToken token)
    {
        if (!CorrectableDocumentPublicationFailures.Contains(publication.ReasonCode) ||
            !string.Equals(task.CardType, "documento", StringComparison.Ordinal))
        {
            return false;
        }

        var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
        var attempt = ApprovedDocumentCatalogPublisher.SelectDeliveredAttempt(attempts);
        var instructions = await board.ListInstructionsAsync(tenantId, task.Id, null, 100, token);
        if (attempt is null || instructions.Count == 0)
        {
            return false;
        }

        var correctionTitle = $"{task.Title} — atualização gate-{attempt.Id}";
        var projectTasks = await board.ListTasksAsync(
            tenantId, project.Id, null, null, 500, token);
        var correction = projectTasks.FirstOrDefault(candidate =>
            string.Equals(candidate.Title, correctionTitle, StringComparison.Ordinal));
        if (correction is null)
        {
            var now = clock.UtcNow;
            var correctionId = UlidValue.New(now).ToString();
            var instructionId = UlidValue.New(now.AddMilliseconds(1)).ToString();
            var detail = string.IsNullOrWhiteSpace(publication.Detail)
                ? publication.ReasonCode
                : publication.Detail.Trim();
            var sourcePath = publication.SourcePath ?? "(artefato não identificado)";
            var body = $"""
                {instructions[^1].Body}

                # Correção obrigatória do gate documental
                - Card substituído: `{task.Id}`.
                - Tentativa aprovada, mas não publicável: `{attempt.Id}`.
                - Branch da versão recusada: `task/agent-run-{attempt.Id.ToLowerInvariant()}`.
                - Artefato recusado: `{sourcePath}`.
                - Código do gate: `{publication.ReasonCode}`.
                - Diagnóstico acionável: {detail}

                Recupere a versão recusada com Git, preserve o conteúdo válido e produza uma nova
                versão que corrija integralmente o diagnóstico. Não marque a versão anterior como
                entregue. Registre no documento o delta e a origem desta atualização.
                """;
            var created = await board.CreateTaskAsync(
                new BoardTaskCreateCommand(
                    tenantId,
                    correctionId,
                    project.Id,
                    task.DemandId,
                    task.BackingDemandId,
                    task.BackingSolicitationId,
                    actorProfileId,
                    correctionTitle,
                    task.Priority,
                    null,
                    task.DueAt,
                    instructionId,
                    body,
                    now,
                    task.PhaseName,
                    "documento"),
                token);
            correction = await board.MoveTaskAsync(
                new BoardTaskMoveCommand(
                    tenantId,
                    created.Task.Id,
                    "ready",
                    $"correção do gate documental do card {task.Id}",
                    "system",
                    now.AddMilliseconds(2)),
                token);
            LogDocumentCorrectionCreated(
                logger, task.Id, correction.Id, publication.ReasonCode);
        }
        else if (string.Equals(correction.State, "backlog", StringComparison.Ordinal))
        {
            correction = await board.MoveTaskAsync(
                new BoardTaskMoveCommand(
                    tenantId,
                    correction.Id,
                    "ready",
                    $"retomada da correção do gate documental do card {task.Id}",
                    "system",
                    clock.UtcNow),
                token);
        }

        var reason = string.Concat(
            BoardTaskDismissalPolicy.DocumentGateReasonPrefix,
            publication.ReasonCode,
            "; replacement:",
            correction.Id,
            string.IsNullOrWhiteSpace(publication.Detail)
                ? string.Empty
                : $"; {publication.Detail}");
        _ = await board.DismissTaskAsync(
            new BoardTaskDismissCommand(
                tenantId,
                task.Id,
                reason.Length <= 2_000 ? reason : reason[..2_000],
                "system",
                clock.UtcNow),
            token);
        return true;
    }

    /// <summary>Ator registrado na cadeia quando quem integra é a chefe, não um humano.</summary>
    public const string ChiefIntegrationActor = "chief";

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: card {TaskId} integrado ({Branch}).")]
    private static partial void LogCardIntegrated(ILogger logger, string taskId, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: integração do card {TaskId} adiada: {ReasonCode}.")]
    private static partial void LogCardIntegrationRefused(ILogger logger, string taskId, string reasonCode);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chief: documento do card {TaskId} publicado no catálogo ({DocumentId}/{VersionId}).")]
    private static partial void LogDocumentPublished(
        ILogger logger, string taskId, string documentId, string versionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Chief: publicação documental do card {TaskId} recusada: {ReasonCode}.")]
    private static partial void LogDocumentPublicationRefused(
        ILogger logger, string taskId, string reasonCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Chief: card documental {TaskId} substituído pelo card de correção {CorrectionTaskId} após o gate {ReasonCode}.")]
    private static partial void LogDocumentCorrectionCreated(
        ILogger logger, string taskId, string correctionTaskId, string reasonCode);

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
            token.ThrowIfCancellationRequested();
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
    private const string ReplanMarker = ReplanAttemptPolicy.ReplanMarker;

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
    /// <summary>
    /// Chave de idempotência do replanejamento. Identifica o COMANDO — card, versão, conteúdo e a
    /// versão de instrução que ele cria. O id da instrução precisa entrar: sem ele, duas rodadas
    /// com intenção idêntica apresentavam a mesma chave com cargas diferentes, e o inbox — que
    /// guarda também as mutações RECUSADAS — devolvia conflito para sempre depois da primeira
    /// recusa.
    /// </summary>
    internal static string ReplanIdempotencyKey(
        string taskId,
        long taskVersion,
        string contentHash,
        string instructionVersionId) =>
        $"chief-loop-replan:{taskId}:v{taskVersion}:{contentHash[..12]}:{instructionVersionId}";

    private async Task<bool> TryReplanEscalatedAsync(
        string tenantId,
        ProjectRecord project,
        BoardTaskRecord task,
        string controlledRoot,
        IWorkBoardStore board,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var instructions = await board.ListInstructionsAsync(tenantId, task.Id, null, 100, token);
        if (instructions.Count == 0)
        {
            return false;
        }

        var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);

        // A RODADA é lida do corpo da última instrução, e não contada nas versões. O marcador é
        // HERDADO — a instrução corretiva copia o corpo anterior e acrescenta os achados —, então
        // contar ocorrências media herança em vez de trabalho. Ver ReplanAttemptPolicy.
        var replanRound = ReplanAttemptPolicy.ReadReplanRound(instructions[^1].Body);
        var alreadyReplanned = replanRound > 0;

        // Replanejar uma vez basta para um card que TENTOU e falhou: reescrever a instrução de novo
        // sobre a mesma abordagem só repete o fracasso. Mas um card que NUNCA rodou não tem
        // abordagem anterior a evitar — ele escalou por causa operacional (despacho impossível), e o
        // replanejamento é o único caminho de volta à fila depois que essa causa é resolvida.
        // Prendê-lo na guarda criava um beco sem saída: foi assim que os cinco assentos do Conselho
        // ficaram travados mesmo depois de a causa ter sido corrigida. O teto de rodadas impede que
        // a exceção vire outro laço.
        //
        // "TENTOU" precisa significar que uma abordagem foi de fato exercida. Uma tentativa morta
        // por infraestrutura — reinício do Host, conta sem cota — nunca chegou a julgar o
        // enunciado: ela terminou sem produzir nada. Contá-la aqui gastava o único
        // replanejamento do card por culpa alheia e o deixava escalado para sempre, com o dono
        // como unica saida. Foi o que prendeu os cards de Arquitetura do E2E de emprestimos,
        // cujas tres tentativas morreram todas na mesma conta com a cota estourada.
        // E "produziu nada" nao e so morrer por infraestrutura. Uma tentativa que RODOU, gastou
        // token e entregou DIFF VAZIO tambem nunca exerceu a abordagem — nao ha o que evitar
        // repetir, porque nada foi tentado. Medido em 04/08: os quatro cards de implementacao
        // entregaram vazio duas vezes cada, por um defeito NOSSO no cabecalho da instrucao
        // (OPS-069) e pela decisao de arquitetura sem caminho executavel (OPS-072). O orcamento
        // anti-laco os puniu por uma parede que nao era deles, e o replanejamento — a unica volta
        // — ja estava gasto quando os consertos entraram no ar.
        //
        // A regra continua estreita de proposito: ela vale para a guarda do REPLANEJAMENTO, e nao
        // para o orcamento de rodadas nem para o limiar do circuito. Quem impede o laco aqui e o
        // teto de replanejamentos logo abaixo, que agora conta o que diz contar.
        // O SINAL DE "DEIXOU COMMIT" NÃO ESTAVA EM `CommitRefs`, e medir isso ao vivo derrubou a
        // primeira versão desta regra. A colheita governada commita os restos da worktree e
        // devolve o HEAD dela mesmo quando não havia resto nenhum — então TODA tentativa colhida
        // grava `agent-run:`, `git-branch:` e `git-commit:`, e `CommitRefs.Count > 0` é sempre
        // verdadeiro. Medido nos quatro cards da fase 5: as onze tentativas têm três referências
        // cada, inclusive as de diff vazio, e o SHA gravado para as vazias é o de um merge de OUTRA
        // tentativa. A regra escrita para devolvê-los à fila nunca disparava — um proxy que
        // quebrou, exatamente como a contagem de versões de instrução logo abaixo.
        //
        // A prova de entrega é o DIFF da branch da tentativa. `null` significa que não deu para
        // apurar (branch ausente, git com erro) e conta como ENTREGOU: afrouxar uma guarda
        // anti-laço por causa de uma leitura que falhou é o pior default possível aqui.
        var realAttempts = 0;
        foreach (var attempt in attempts)
        {
            // O diff só é apurado para quem passou nos dois sinais baratos: ler git por tentativa
            // morta de cota seria custo sem pergunta.
            if (!ReplanAttemptPolicy.ExercisedApproach(attempt.FailureReason, attempt.TokensOutput, null))
            {
                continue;
            }

            var introduced = await AttemptIntroducedChangesAsync(
                project, controlledRoot, attempt.Id, token);
            if (ReplanAttemptPolicy.ExercisedApproach(
                    attempt.FailureReason, attempt.TokensOutput, introduced))
            {
                realAttempts++;
            }
            else
            {
                LogReplanEmptyDelivery(logger, task.Id, attempt.Id);
            }
        }

        // O TETO CONTA REPLANEJAMENTOS, e nao versoes de instrucao.
        //
        // Ele existe para limitar quantas vezes um card volta a fila por causa operacional, e a
        // contagem de instrucoes era um proxy: cada replanejamento grava uma. Mas a correcao apos
        // reprovacao tambem grava, e o proxy quebrou — um card com dez versoes corretivas e UM
        // replanejamento aparecia como orcamento esgotado. Os quatro cards da fase 5 estavam
        // exatamente assim: acusados de ter gastado quatro rodadas operacionais tendo usado uma.
        if (alreadyReplanned &&
            (realAttempts > 0 || replanRound >= MaximumOperationalReplanRounds))
        {
            return false;
        }

        var rejected = attempts.Count(attempt =>
            string.Equals(attempt.State, "failed", StringComparison.Ordinal));
        // O MESMO cabeçalho derivável da correção, pela MESMA razão — e este é o caminho que mais
        // precisa dele. O replanejamento é a última via de volta de um card escalado: se ele
        // recopiar um roteamento errado, o card volta à fila para repetir exatamente o fracasso
        // que o escalou, e gasta o único replanejamento que tinha. Corrigir só a correção
        // (OPS-069) teria deixado os três cards bloqueados da fase 5 exatamente onde estavam.
        var previousBody = instructions[^1].Body;
        var replanResolution = ChiefCardResolver.Resolve(
            task.Title, previousBody, [], task.Priority);
        // O bloco de replanejamento SUBSTITUI o anterior em vez de se somar a ele. Acumulando,
        // o ator recebia cinco cópias idênticas da mesma ordem — "a abordagem anterior NÃO deve
        // ser repetida... registre o bloqueio em vez de tentar de novo" — sem nada que dissesse
        // que eram a mesma frase repetida. Um enunciado que cresce por acréscimo a cada volta não
        // é o enunciado que alguém escolheu mandar.
        var content =
            RebuildInstructionHeader(
                ReplanAttemptPolicy.StripReplanBlocks(previousBody),
                replanResolution.Role, replanResolution.PersonaKey, task.CardType) +
            $"\n\n{ReplanMarker} (rodada {replanRound + 1})\n" +
            $"A abordagem anterior esgotou os ciclos de revisão ({rejected} reprovação(ões)) e NÃO " +
            "deve ser repetida como está. Antes de escrever qualquer código:\n" +
            "1. Releia os achados da revisão e diga, em uma linha, por que a abordagem anterior " +
            "não fechou.\n" +
            "2. Reduza o card ao MENOR incremento verificável que satisfaça os critérios de aceite " +
            "— entregar menos, com evidência, vale mais que entregar tudo sem evidência.\n" +
            "3. Produza a evidência que faltou (execução de teste, log, diff) junto com a mudança.\n" +
            "Se após isto o escopo ainda não couber, registre o bloqueio em vez de tentar de novo.";

        var now = clock.UtcNow;
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content)));

        // A chave de idempotência precisa acompanhar o CONTEÚDO. Enquanto era só o id do card, o
        // mesmo card em outro ciclo apresentava a mesma chave com uma instrução diferente (o texto
        // embute a contagem de reprovações e a versão do card): a cadeia recusava com
        // `IdempotencyConflictException` e a exceção derrubava o ciclo INTEIRO do projeto — colheita,
        // review, integração e avisos, para todos os cards. Com o hash na chave, repetir o mesmo
        // replanejamento é replay idempotente e um replanejamento realmente diferente é uma
        // mutação nova.
        // O id da nova versão de instrução é sorteado a cada rodada — e é ele que faz o COMANDO
        // ser diferente mesmo quando a intenção é idêntica. Deixá-lo fora da chave prometia uma
        // estabilidade que a carga não tinha: o inbox guardava chave+hash da primeira recusa e
        // toda rodada seguinte chegava com a mesma chave e um hash novo, ou seja, conflito
        // permanente. Foi o terceiro cadeado dos seis assentos do Conselho, depois do estado
        // inválido: a causa técnica sumia, o card seguia parado.
        //
        // A chave identifica o COMANDO, não a intenção. Repetir literalmente o mesmo comando
        // continua sendo replay idempotente; um comando novo é uma mutação nova, e quem barra a
        // repetição indevida é a precondição de estado — o card já replanejado não está mais
        // escalado e o segundo replanejamento é recusado por ela, como deve.
        var instructionVersionId = UlidValue.New(now).ToString();
        WorkChainMutationReceipt receipt;
        try
        {
            receipt = await chain.ReplanEscalatedTaskAsync(
                new WorkTaskReplanCommand(
                    tenantId,
                    task.BackingSolicitationId,
                    task.Id,
                    instructionVersionId,
                    content,
                    contentHash,
                    project.ChiefAgentId,
                    "chief.replan_after_escalation",
                    $"attempts:{rejected}",
                    task.Version,
                    ReplanIdempotencyKey(
                        task.Id, task.Version, contentHash, instructionVersionId),
                    now),
                token);
        }
        catch (IdempotencyConflictException)
        {
            // Defesa em profundidade: um conflito de chave num único card não pode calar o
            // acompanhamento de todos os outros. Ele fica registrado e o card segue escalado.
            LogCardEscalated(logger, task.Id, "replanejamento recusado: conflito de idempotência");
            return false;
        }

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
            token.ThrowIfCancellationRequested();
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

            // O CABEÇALHO É FATO DERIVÁVEL, NÃO HISTÓRIA A PRESERVAR.
            //
            // Copiar o corpo anterior inteiro carregava para a frente as três linhas de
            // roteamento — papel, persona e tipo de card —, e com elas qualquer defeito que
            // estivesse lá desde a versão 1. Medido em 04/08 (OPS-069): os cards de implementação
            // nasceram seis minutos antes do conserto que tirava a persona de descoberta da fatia
            // de código, e chegaram à décima versão de instrução ainda mandando um Product Owner
            // — cujo escopo NEGA `src/**` — implementar backend. O ator obedeceu a persona e
            // entregou diff vazio, duas vezes, em três cards. Um defeito no cabeçalho original
            // era imortal dentro do card: a correção o recopiava a cada reprovação, e nem o
            // replanejamento o alcançava.
            //
            // Agora a correção reconstrói o cabeçalho do estado ATUAL e preserva apenas o corpo,
            // que é onde mora o trabalho de verdade. Achado do crítico continua sendo acrescentado
            // ao fim, nunca sobrescrevendo nada.
            var previous = instructions[^1].Body;
            var correctionResolution = ChiefCardResolver.Resolve(
                task.Title, previous, [], task.Priority);
            var content =
                RebuildInstructionHeader(
                    previous,
                    correctionResolution.Role,
                    correctionResolution.PersonaKey,
                    task.CardType) +
                $"\n\n## Correções exigidas pelo review independente (tentativa {rejected.Id})\n{findings}\n" +
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

    /// <summary>
    /// Fase 1B: o orçamento do card, lido do PLANO que o originou. Cards sem plano (criados à mão)
    /// e planos anteriores a este bloco devolvem nulo e seguem pela regra anterior — o orçamento
    /// governa onde ele existe, sem inventar teto para trabalho que ninguém orçou.
    /// </summary>
    private static async Task<DemandCardBudget?> ReadCardBudgetAsync(
        string tenantId, BoardTaskRecord task, IDemandPlanStore plans, CancellationToken token)
    {
        if (task.DemandId is { Length: > 0 } demandId)
        {
            var plan = await plans.GetByDemandAsync(tenantId, demandId, token);
            var code = DemandDecompositionPlanner.CodeOf(task.Title);
            var planned = plan?.Cards
                .FirstOrDefault(card =>
                    string.Equals(
                        DemandDecompositionPlanner.CodeOf(card.ProposedTitle), code,
                        StringComparison.OrdinalIgnoreCase))
                ?.Budget;
            if (planned is not null)
            {
                return planned;
            }
        }

        // SEM plano ainda há teto. Cards que não nascem de uma demanda — o artefato da esteira, o
        // parecer do conselho — ficavam sem orçamento nenhum, e sem orçamento não há esgotamento:
        // um card cuja execução é recusada volta para a fila indefinidamente. Foi o que aconteceu
        // no piloto real: vinte e uma tentativas do mesmo card, canceladas em milissegundos, sem
        // que nada as interrompesse.
        //
        // O teto padrão é o mesmo que a EffortPolicy daria a um trabalho pequeno de um agente só,
        // que é o que esses cards são. Quem gasta as rodadas ESCALA, com o fato auditado.
        return DefaultCardBudget;
    }

    /// <summary>
    /// Orçamento de quem não vem de plano. Deliberadamente curto: um card de esteira que precisa
    /// de mais de três rodadas não está com falta de tentativa, está com um problema que outra
    /// tentativa não resolve.
    /// </summary>
    private static readonly DemandCardBudget DefaultCardBudget = new(
        Agents: 1,
        TokenBudget: 8000,
        MaxRounds: 3,
        ReviewDepth: 1,
        FanOutAllowed: false,
        ReasonCode: "effort.default_off_plan");

    /// <summary>
    /// Conta somente rodadas que chegaram a executar. O registro durável nasce antes da aquisição
    /// do workspace para que a compensação seja rastreável; por isso uma recusa de agendamento
    /// (por exemplo, <c>workspace.scopeconflict</c>) aparece no histórico como <c>cancelled</c>.
    /// Queimar orçamento com esse registro transforma contenção normal em falso esgotamento.
    /// <c>queued</c> também não executou; os demais estados representam trabalho em curso ou uma
    /// execução que de fato terminou e, portanto, consomem uma rodada.
    /// </summary>
    public static int CountSpentRounds(IReadOnlyList<BoardAttemptRecord> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        return attempts.Count(attempt =>
            !string.Equals(attempt.State, "queued", StringComparison.Ordinal) &&
            !string.Equals(attempt.State, "cancelled", StringComparison.Ordinal));
    }

    /// <summary>
    /// O card esgotou as rodadas orçadas. Parar em silêncio esconderia um card morto no board;
    /// seguir despachando queimaria cota repetindo o mesmo fracasso. A saída é ESCALAR com a
    /// evidência: quantas rodadas foram orçadas, quantas foram gastas e por qual razão o orçamento
    /// era aquele.
    /// </summary>
    private async Task EscalateBudgetExhaustionAsync(
        string tenantId,
        string projectId,
        BoardTaskRecord task,
        IWorkChainStore chain,
        DemandCardBudget budget,
        int spentRounds,
        CancellationToken token)
    {
        LogBudgetExhausted(logger, task.Id, spentRounds, budget.MaxRounds, budget.ReasonCode);
        Observability.PoseidonTelemetry.RecordEffortBudget("exhausted", budget.ReasonCode);

        // O comentário do chamador promete que o card "ESCALA, com o fato auditado" — mas só o
        // fato era auditado. Sem mudar o estado, o card continuava em `ready`: reexaminado a cada
        // ciclo, nunca anunciado ao dono (o anúncio filtra por `escalated`) e reescrevendo o mesmo
        // evento de auditoria a cada tique do laço. É exatamente o sintoma que o ramo do circuito
        // aberto, logo acima, já havia corrigido — card morto e invisível para todos.
        _ = await chain.EscalateUndispatchableTaskAsync(
            new WorkTaskUndispatchableCommand(
                tenantId, task.BackingSolicitationId, task.Id,
                $"Este trabalho já consumiu as {budget.MaxRounds} rodadas que orçamos para ele " +
                $"({spentRounds} usadas) e ainda não fechou. Continuar tentando gastaria esforço " +
                "repetindo a mesma abordagem, então parei: preciso rever o enunciado, reduzir o " +
                "escopo ou aumentar o orçamento antes de seguir.",
                $"card:{task.Id}",
                task.Version,
                // A versão entra na chave: o inbox guarda também as mutações RECUSADAS, e uma
                // chave fixa envenenaria toda tentativa posterior com conflito de idempotência.
                $"chief-loop-budget-exhausted:{task.Id}:{task.Version}",
                clock.UtcNow),
            token);
        using var scope = scopes.CreateScope();
        var audit = scope.ServiceProvider
            .GetRequiredService<global::Harness.Persistence.Abstractions.Governance.IAuditEventStore>();
        await audit.AppendAsync(
            new global::Harness.Persistence.Abstractions.Governance.AuditEventAppendCommand(
                tenantId,
                "system",
                null,
                "card.effortBudgetExhausted",
                "task",
                task.Id,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    projectId,
                    taskId = task.Id,
                    spentRounds,
                    budget.MaxRounds,
                    budget.Agents,
                    budget.TokenBudget,
                    budget.ReviewDepth,
                    budget.ReasonCode,
                }),
                clock.UtcNow),
            token);
    }

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
            token.ThrowIfCancellationRequested();
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
                token.ThrowIfCancellationRequested();
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
    /// <summary>
    /// Existe no ELENCO um crítico que pode revisar este ator — ignorando se ele está livre agora.
    ///
    /// É a pergunta que separa "ninguém está disponível neste instante" de "ninguém serve para
    /// isto". A primeira se resolve esperando; a segunda, não. <see cref="SelectCriticAliases"/>
    /// já descartava ambas pelo mesmo caminho e devolvia uma lista vazia idêntica nos dois casos —
    /// e foi essa perda de informação que fez a falta momentânea de revisor virar impedimento
    /// permanente do card.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> quando ninguém no elenco serve este ator; o próprio
    /// <paramref name="now"/> quando existe candidato sem restrição declarada; e a MENOR janela de
    /// retorno declarada pelo provedor quando todos os candidatos estão em cota ou resfriamento.
    /// </returns>
    internal DateTimeOffset? CriticRosterReturnsBy(string producerAlias, DateTimeOffset now)
    {
        var decision = _scheduler.Select(accounts, BuildCriticSchedulingRequest(producerAlias, now));
        var eligible = decision.Candidates.Where(candidate => candidate.Eligible).ToArray();
        if (eligible.Length > 0)
        {
            return now;
        }

        DateTimeOffset? earliest = null;
        foreach (var candidate in decision.Candidates)
        {
            if (candidate.Eligible ||
                (!string.Equals(candidate.ReasonCode, "account.quota_limited", StringComparison.Ordinal) &&
                 !string.Equals(candidate.ReasonCode, "account.cooling_down", StringComparison.Ordinal)))
            {
                continue;
            }

            var account = accounts.Get(candidate.Alias);
            if (account?.CooldownUntil is { } until && until > now &&
                (until < earliest || earliest is null))
            {
                earliest = until;
            }
        }

        return earliest;
    }

    internal string[] SelectCriticAliases(string producerAlias, DateTimeOffset now)
    {
        var decision = _scheduler.Select(accounts, BuildCriticSchedulingRequest(producerAlias, now));
        return decision.Candidates
            .Where(candidate => candidate.Eligible)
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Alias, StringComparer.Ordinal)
            .Select(candidate => candidate.Alias)
            .ToArray();
    }

    private AccountSchedulingRequest BuildCriticSchedulingRequest(
        string producerAlias, DateTimeOffset now) =>
        new()
        {
            Role = AgentRoles.Critic,
            RequiredCapability = "review",
            Now = now,
            ForCritic = true,
            ActorAlias = producerAlias,
            Quotas = CapacitySignals(now),
        };

    /// <summary>
    /// Colheita git da tentativa: se a worktree sobreviveu (worker terminou sem commitar), commita
    /// os restos na branch da tentativa e remove a worktree. Nunca destrói trabalho; falha aqui é
    /// logada e não impede a colheita da cadeia (o diff apenas refletirá o que está na branch).
    /// </summary>
    /// <summary>
    /// Quantas rodadas o card de fato gastou: as tentativas que EXERCERAM a abordagem. Custa uma
    /// leitura de git por tentativa candidata, então só deve ser chamado quando a contagem crua
    /// já apontou esgotamento — o refinamento nunca aumenta o número.
    /// </summary>
    private async Task<int> CountExercisedRoundsAsync(
        ProjectRecord project,
        string controlledRoot,
        IReadOnlyList<BoardAttemptRecord> attempts,
        CancellationToken token)
    {
        var exercised = 0;
        foreach (var attempt in attempts)
        {
            if (string.Equals(attempt.State, "queued", StringComparison.Ordinal) ||
                string.Equals(attempt.State, "cancelled", StringComparison.Ordinal))
            {
                continue;
            }

            if (!ReplanAttemptPolicy.ExercisedApproach(
                    attempt.FailureReason, attempt.TokensOutput, null))
            {
                continue;
            }

            if (ReplanAttemptPolicy.ExercisedApproach(
                    attempt.FailureReason,
                    attempt.TokensOutput,
                    await AttemptIntroducedChangesAsync(project, controlledRoot, attempt.Id, token)))
            {
                exercised++;
            }
        }

        return exercised;
    }

    /// <summary>
    /// A tentativa introduziu mudança na branch dela? <c>null</c> quando não deu para apurar —
    /// quem chama decide o que fazer com a dúvida, em vez de recebê-la disfarçada de "não".
    /// </summary>
    private async Task<bool?> AttemptIntroducedChangesAsync(
        ProjectRecord project,
        string controlledRoot,
        string attemptId,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return null;
        }

        try
        {
            var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl);
            using var manager = await GitWorktreeManager.OpenAsync(repositoryRoot, controlledRoot, token);
            return await manager.BranchIntroducedChangesAsync(
                $"task/agent-run-{attemptId.ToLowerInvariant()}", token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAttemptDiffUnavailable(logger, attemptId, exception.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chief: card {TaskId} — tentativa {AttemptId} não introduziu mudança nenhuma na branch; não exerceu abordagem e não gasta o replanejamento.")]
    private static partial void LogReplanEmptyDelivery(ILogger logger, string taskId, string attemptId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Chief: não foi possível apurar o diff da tentativa {AttemptId} ({Error}); ela conta como entrega para não afrouxar a guarda de replanejamento.")]
    private static partial void LogAttemptDiffUnavailable(ILogger logger, string attemptId, string error);

    private async Task<string?> TryHarvestWorktreeAsync(
        ProjectRecord project,
        string controlledRoot,
        string attemptId,
        string branchName,
        CancellationToken token)
    {
        var worktreePath = System.IO.Path.Combine(controlledRoot, "worktrees", attemptId);
        if (!System.IO.Directory.Exists(worktreePath))
        {
            return null;
        }

        try
        {
            var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl!);
            using var manager = await GitWorktreeManager.OpenAsync(repositoryRoot, controlledRoot, token);
            var commit = await manager.CommitWorktreeLeftoversAsync(
                worktreePath, $"chore(harness): colheita da tentativa {attemptId}", token);
            _ = await manager.RemoveTaskWorktreeAsync(branchName, worktreePath, deleteBranch: false, token);
            return commit;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogWorktreeHarvestFailure(logger, attemptId, exception.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: run da tentativa {AttemptId} colhido — card {TaskId} aguarda review.")]
    private static partial void LogRunHarvested(ILogger logger, string taskId, string attemptId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: run {Status} da tentativa {AttemptId} — card {TaskId} devolvido a `ready`.")]
    private static partial void LogRunRequeued(ILogger logger, string taskId, string attemptId, string status);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliação do projeto {ProjectId}: {Scanned} de {Total} card(s) em desenvolvimento, {Running} em execução.")]
    private static partial void LogReconcileScan(
        ILogger logger, string projectId, int scanned, int total, int running);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Reconciliação: card {TaskId} está `running` mas nenhuma de suas {AttemptCount} tentativa(s) está `running` — estado inconsistente.")]
    private static partial void LogReconcileNoRunningAttempt(
        ILogger logger, string taskId, int attemptCount);

    /// <summary>Execução nula significa que o workspace já sumiu — a tentativa é órfã.</summary>
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Reconciliação: card {TaskId}, tentativa {AttemptId}, execução {RunStatus}.")]
    private static partial void LogReconcileSubject(
        ILogger logger, string taskId, string attemptId, AgentRunStatus? runStatus);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: review da tentativa {AttemptId} (card {TaskId}) por {CriticAlias}: {Decision}.")]
    private static partial void LogReviewApplied(ILogger logger, string taskId, string attemptId, string criticAlias, string decision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: review da tentativa {AttemptId} (card {TaskId}) adiado por falha de infraestrutura: {ReasonCode}.")]
    private static partial void LogReviewInfrastructureFailure(ILogger logger, string taskId, string attemptId, string reasonCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: nenhum crítico disponível ≠ ator {ProducerAlias} para o card {TaskId}; review adiado.")]
    private static partial void LogNoCriticAvailable(ILogger logger, string taskId, string producerAlias);

    // A dispensa precisa ser VISÍVEL. Um card que fecha sem revisão independente é exatamente o
    // que o produto promete que não acontece; a exceção é legítima e delimitada, e por isso é dita
    // em voz alta uma vez por parecer, em vez de virar silêncio no meio de um ciclo.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chief: card {TaskId} é parecer do Conselho — tentativa {AttemptId} aceita pela consolidação, sem revisão por par (D1).")]
    private static partial void LogCouncilOpinionAccepted(ILogger logger, string taskId, string attemptId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: card {TaskId} ({Code}) promovido a `ready` pela triagem por ondas.")]
    private static partial void LogCardPromoted(ILogger logger, string taskId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: colheita da worktree da tentativa {AttemptId} falhou: {ErrorType}.")]
    private static partial void LogWorktreeHarvestFailure(ILogger logger, string attemptId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: acompanhamento do projeto {ProjectId} falhou neste ciclo: {ErrorType}.")]
    private static partial void LogFollowUpFailure(
        ILogger logger,
        Exception exception,
        string projectId,
        string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} adiado: {ReasonCode} (volta: {RetryAfter}; contas: {Candidates}).")]
    private static partial void LogCardDeferred(ILogger logger, string taskId, string reasonCode, string retryAfter, string candidates);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Chief: card {TaskId} adiado antes da tentativa — escopo ocupado por run vivo.")]
    private static partial void LogCardScopeDeferred(ILogger logger, string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chief: card {TaskId} ADIADO — perfil da conta {AccountAlias} ocupado (chief.account_profile_busy); nenhuma tentativa foi gasta.")]
    private static partial void LogCardProfileBusyDeferred(
        ILogger logger, string taskId, string accountAlias);

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
        string actorProfileId,
        ProjectRecord project,
        ChiefCardResolution resolution,
        string accountAlias,
        string? model,
        string? effort,
        BoardTaskRecord task,
        string instructionVersionId,
        IReadOnlyList<AgentDefinitionRecord> personas,
        IAgentCatalogStore catalog,
        string controlledRoot,
        bool chiefReinforcement,
        IWorkBoardStore board,
        IWorkChainStore chain,
        CancellationToken token)
    {
        var now = clock.UtcNow;

        // Resolve a pessoa ANTES de gravar a atribuição. A conta executora é infraestrutura e
        // continua registrada na tentativa; o responsável do card é a instância da persona no
        // projeto. Antes os dois conceitos eram colapsados e a interface mostrava um alias de
        // provider (ou "Aguardando organização") no lugar do Product Owner/Arquiteto/QA.
        var persona = FindPersona(personas, resolution.PersonaKey);
        if (persona is not null && !IsEligible(persona, project.Id, task.Priority))
        {
            LogPersonaNotEligible(logger, task.Id, persona.Key);
            persona = null;
        }

        if (persona is null && !string.Equals(
                resolution.PersonaKey, resolution.InferredPersonaKey, StringComparison.OrdinalIgnoreCase))
        {
            LogPersonaNotInCatalog(logger, task.Id, resolution.PersonaKey);
            persona = FindPersona(personas, resolution.InferredPersonaKey);
        }

        var assigneeAgentId = accountAlias;
        if (persona is not null)
        {
            var route = await catalog.GetAgentAsync(tenantId, project.ChiefAgentId, token);
            if (route is not null &&
                !string.IsNullOrWhiteSpace(route.AccountId) &&
                !string.IsNullOrWhiteSpace(route.ModelId) &&
                !string.IsNullOrWhiteSpace(route.Effort) &&
                !string.IsNullOrWhiteSpace(route.ProviderEffortValue))
            {
                var ensured = await catalog.EnsureProjectAgentAsync(
                    new ProjectAgentEnsureCommand(
                        tenantId,
                        actorProfileId,
                        UlidValue.New(now).ToString(),
                        project.Id,
                        persona.Id,
                        persona.Name,
                        route.AccountId,
                        route.ModelId,
                        route.Effort,
                        route.ProviderEffortValue,
                        route.FallbackModelIds ?? [],
                        $"Delegação do card {task.Id} para {persona.Key}.",
                        now),
                    token);
                assigneeAgentId = ensured.Agent.Id;
            }
        }

        // Inicia uma tentativa durável na cadeia de trabalho (id nunca solto).
        var attemptId = UlidValue.New(now).ToString();
        var assigned = await chain.AssignTaskAsync(
            new WorkTaskAssignmentCommand(
                tenantId,
                task.BackingSolicitationId,
                task.Id,
                instructionVersionId,
                assigneeAgentId,
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
                assigneeAgentId, assigned.TaskVersion!.Value, $"chief-loop-heartbeat:{attemptId}", now),
            token);
        if (started.Status is not (WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay))
        {
            LogAttemptNotStarted(logger, task.Id, started.Status.ToString());
            return false;
        }

        var briefing = persona is null
            ? resolution.Card.Scope
            : PersonaCardComposer.Compose(ToContent(persona), resolution.Card);

        // Correção precisa começar do patch reprovado, não novamente do branch principal. A
        // colheita mantém a branch da tentativa para review; usá-la como base preserva o arquivo
        // e o delta que o critic mandou corrigir. Se uma colheita excepcional não deixou a
        // branch, o fallback HEAD mantém o card recuperável e a instrução ainda contém os achados.
        var priorAttempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, token);
        var correctionBranch = CorrectionBaseBranch(priorAttempts);
        if (correctionBranch is not null)
        {
            using var repository = await GitWorktreeManager.OpenAsync(
                System.IO.Path.GetFullPath(project.RepositoryUrl!), controlledRoot, token);
            var branches = await repository.ListLocalBranchesAsync(token);
            if (!branches.Contains(correctionBranch, StringComparer.Ordinal))
            {
                correctionBranch = null;
            }
        }

        var pathScopeKind = AgentPathScopePolicy.KindForRole(resolution.Role);

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
                BaseReference = correctionBranch ?? "HEAD",
                ScopeClaims = resolution.ScopeClaims,
                Owner = "chief-backlog-loop",
                IdempotencyKey = $"chief-loop:{attemptId}",
                PathScopeKind = pathScopeKind,
                Access = ExternalAgentAccess.Workspace,
                Model = model,
                Effort = effort,
                RiskTier = resolution.Card.RiskTier,
                AcceptanceCriteria = resolution.Card.AcceptanceCriteria,
                // null é deliberadamente fail-closed no orquestrador: uma persona resolvida que
                // declara zero ferramentas é diferente de não ter resolvido persona alguma.
                RequiredToolIds = persona?.ToolIds,
                PersonaKey = persona?.Key,
                ChiefReinforcement = chiefReinforcement,
            },
            token);

        if (snapshot.Status is AgentRunStatus.Rejected or AgentRunStatus.ScopeConflict)
        {
            var rejection = snapshot.FinalError ?? "rejeitado sem motivo declarado";
            LogRunRejected(logger, task.Id, snapshot.Status.ToString(), rejection);
            _dispatchBackoff[task.Id] = clock.UtcNow.Add(RejectedDispatchBackoff);

            // O MOTIVO precisa ficar visível para o dono, não só no log do servidor.
            //
            // Uma recusa que só existe em log produz o pior sintoma possível: vinte e uma
            // tentativas canceladas em milissegundos, nenhuma com razão registrada, e um card que
            // volta para a fila para sempre. De fora, a fábrica parece trabalhar e não produz
            // nada. Falha de auditoria nunca impede o resto do ciclo — perder o registro é ruim,
            // parar a fábrica por causa dele é pior.
            try
            {
                using var auditScope = scopes.CreateScope();
                await auditScope.ServiceProvider
                    .GetRequiredService<global::Harness.Persistence.Abstractions.Governance.IAuditEventStore>()
                    .AppendAsync(
                        new global::Harness.Persistence.Abstractions.Governance.AuditEventAppendCommand(
                            tenantId, "agent", accountAlias, "card.dispatchRejected",
                            "task", task.Id,
                            $"{{\"status\":\"{snapshot.Status}\",\"reason\":\"{rejection}\"}}",
                            clock.UtcNow),
                        token);
            }
            catch (Exception auditFailure) when (auditFailure is not OperationCanceledException)
            {
                _ = auditFailure;
            }

            // COMPENSAÇÃO: a tentativa durável já estava `running` quando o orquestrador recusou
            // o run (ex.: conflito de claim com um run vivo). Sem abandoná-la, o card ficaria em
            // `development` com uma tentativa órfã PARA SEMPRE. O abandono devolve o card a
            // `ready` e o próximo ciclo re-tenta quando o claim liberar.
            //
            // O MOTIVO viaja com a compensação. O campo sempre existiu no contrato e o chamador
            // nunca o preenchia: a tentativa nascia e morria com `failure_reason` NULO, e o
            // detector de parede — que lê exatamente esse campo — anunciava "sem motivo
            // registrado pelo executor" para uma recusa cujo código o sistema tinha em mãos no
            // instante em que a produziu. É a quinta vez nesta operação que a causa existe e
            // ninguém a escreve.
            _ = await chain.ExpireAttemptLeaseAsync(
                new WorkAttemptLeaseExpiredCommand(
                    tenantId, task.BackingSolicitationId, task.Id, attemptId,
                    started.TaskVersion!.Value, $"chief-loop-compensate:{attemptId}", clock.UtcNow,
                    CountsTowardRoundBudget: false,
                    FailureReason: $"chief.dispatch_rejected: {rejection}"),
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

    /// <summary>Branch colhida da reprovação mais recente, usada como base da correção.</summary>
    public static string? CorrectionBaseBranch(IReadOnlyList<BoardAttemptRecord> attempts)
    {
        var rejected = attempts.LastOrDefault(attempt =>
            string.Equals(attempt.State, "rejected", StringComparison.Ordinal));
        return rejected is null
            ? null
            : $"task/agent-run-{rejected.Id.ToLowerInvariant()}";
    }

    /// <summary>
    /// Localiza marcadores inequívocos de trabalho inacabado somente nas linhas adicionadas do
    /// diff. Texto removido e cabeçalhos do patch não geram falso positivo.
    /// </summary>
    /// <summary>
    /// Marcadores que uma entrega não pode carregar para o review comportamental.
    ///
    /// O casamento é por PALAVRA, não por substring, e a diferença não é cosmética: `TODO:` como
    /// substring casa dentro de `metodo:` — e este produto escreve identificador em português.
    /// A primeira entrega de CÓDIGO da operação foi barrada por duas linhas de teste que diziam
    /// `{ url, metodo: opcoes?.method }`, e o card ficou irreversivelmente adiando. Um gate
    /// determinístico que reprova o idioma do produto não protege ninguém: ele só transfere para
    /// o ator o custo de adivinhar o que o revisor achou.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenDeliveryPlaceholders(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        string[] markers = ["PENDING_PUB_SHA", "REPLACE_ME", "CHANGEME", "<commit-sha>", "TODO:"];
        return diff.Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            .Where(line => markers.Any(marker => ContainsMarkerAsWord(line, marker)))
            .Select(line => line.Length <= 1000 ? line[1..] : line[1..1000])
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToArray();
    }

    /// <summary>
    /// O marcador aparece na linha como palavra própria? A borda só é exigida do lado em que o
    /// próprio marcador termina em caractere de palavra — `TODO:` fecha em `:`, então só a borda
    /// da esquerda importa; `REPLACE_ME` exige as duas, para não casar dentro de `REPLACE_MENT`.
    /// </summary>
    private static bool ContainsMarkerAsWord(string line, string marker)
    {
        var needsLeftBoundary = IsWordCharacter(marker[0]);
        var needsRightBoundary = IsWordCharacter(marker[^1]);
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var leftOk = !needsLeftBoundary || index == 0 || !IsWordCharacter(line[index - 1]);
            var end = index + marker.Length;
            var rightOk = !needsRightBoundary || end >= line.Length || !IsWordCharacter(line[end]);
            if (leftOk && rightOk)
            {
                return true;
            }

            index = line.IndexOf(marker, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    /// <summary>
    /// Só projeto ATIVO entra no ciclo autônomo. `paused` é decisão explícita do dono (endpoint
    /// `chief/pause`) e `archived` é fim de vida: nenhum dos dois pode consumir cota, slot de
    /// despacho ou disparar review.
    /// </summary>
    private static bool IsDispatchable(ProjectRecord project) =>
        string.Equals(project.State, "active", StringComparison.Ordinal);

    /// <summary>
    /// O projeto está num repositório que a fábrica pode tocar?
    ///
    /// Duas raízes valem, e a segunda faltava:
    ///   * `ControlledRoot` — onde o dono aponta repositórios DELE, que ele já tinha;
    ///   * a raiz GERENCIADA (`&lt;dados&gt;/repositories`) — onde o próprio Poseidon cria o
    ///     repositório de todo projeto novo.
    ///
    /// Sem a segunda, todo projeto criado pelo produto nascia FORA do alcance da própria fábrica:
    /// o dono descrevia a demanda, a chefe planejava, os cards apareciam no quadro e nunca saíam
    /// do lugar — e nada dizia por quê. Exigir que o dono apontasse o `ControlledRoot` para dentro
    /// do diretório de dados do Poseidon seria transferir a ele a correção de um defeito nosso.
    /// </summary>
    private static bool IsInsideControlledRoot(
        ProjectRecord project, string controlledRoot, string managedRepositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return false;
        }

        var repositoryRoot = System.IO.Path.GetFullPath(project.RepositoryUrl);
        if (!System.IO.Directory.Exists(repositoryRoot))
        {
            return false;
        }

        return IsUnder(repositoryRoot, controlledRoot) ||
            IsUnder(repositoryRoot, managedRepositoryRoot);
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalized = System.IO.Path.GetFullPath(root)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return path.StartsWith(
            $"{normalized}{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal);
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
