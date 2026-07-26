using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Providers.Application;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.Agents;
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
    ILogger<ChiefBacklogLoopService> logger) : BackgroundService
{
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chief: card {TaskId} (card_type={CardType}) NÃO despachável — pulado por prontidão (DoR): {Blockers}")]
    private static partial void LogCardNotDispatchable(ILogger logger, string taskId, string cardType, string blockers);

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

        var profileList = await profiles.ListAsync(token);
        if (profileList.Count == 0)
        {
            return (0, 0);
        }

        var profile = profileList[0];

        var controlledRoot = System.IO.Path.GetFullPath(settings.ControlledRoot!);
        var personas = await catalog.ListDefinitionsForTenantAsync(profile.TenantId, null, 100, false, token);

        var dispatched = 0;
        var deferred = 0;

        var projectList = await projects.ListAsync(profile.TenantId, null, 50, token);
        foreach (var project in projectList)
        {
            if (!IsInsideControlledRoot(project, controlledRoot))
            {
                continue;
            }

            // Cards prontos para delegar: board_state `ready` (minúsculo — o enum é case-sensitive
            // no SQLite), não arquivados. Cards nascem em `backlog`; a triagem (humano/DoR) promove
            // a `ready` antes de o loop os enxergar.
            var page = await board.PageTasksAsync(
                profile.TenantId,
                new BoardTaskPageQuery(project.Id, null, null, "ready", null, null, "active", null, 0, 50),
                token);

            var cards = new List<(ChiefCard Card, ChiefCardResolution Resolution, BoardTaskRecord Task, string InstructionVersionId)>();
            foreach (var task in page.Items)
            {
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

                var resolution = ChiefCardResolver.Resolve(
                    task.Title, instructions[^1].Body, [], "medium");
                cards.Add((
                    new ChiefCard(task.Id, project.Id, resolution.Role, resolution.RequiredCapability,
                        PriorityWeight(task.Priority), resolution.ScopeClaims),
                    resolution, task, instructions[^1].Id));
            }

            if (cards.Count == 0)
            {
                continue;
            }

            var plan = policy.Plan(
                [.. cards.Select(entry => entry.Card)], accounts, availability,
                settings.AutoDispatchMaxConcurrent, clock.UtcNow,
                CapacitySignals(clock.UtcNow));
            deferred += plan.Deferred.Count;

            foreach (var decision in plan.Dispatch)
            {
                var entry = cards.First(candidate => candidate.Card.TaskId == decision.Card.TaskId);
                if (await LaunchAsync(
                        profile.TenantId, project, entry.Resolution, decision.AccountAlias,
                        entry.Task, entry.InstructionVersionId, personas, controlledRoot, board, chain, token))
                {
                    dispatched++;
                }
            }
        }

        return (dispatched, deferred);
    }

    private async Task<bool> LaunchAsync(
        string tenantId,
        ProjectRecord project,
        ChiefCardResolution resolution,
        string accountAlias,
        BoardTaskRecord task,
        string instructionVersionId,
        IReadOnlyList<AgentDefinitionRecord> personas,
        string controlledRoot,
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

        // Briefing = PERSONA (do catálogo, pela heurística de planejamento) + CARD (a demanda).
        var persona = personas.FirstOrDefault(definition =>
            string.Equals(definition.Key, resolution.PersonaKey, StringComparison.OrdinalIgnoreCase));
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
                RiskTier = resolution.Card.RiskTier,
                AcceptanceCriteria = resolution.Card.AcceptanceCriteria,
            },
            token);

        if (snapshot.Status is AgentRunStatus.Rejected or AgentRunStatus.ScopeConflict)
        {
            LogRunRejected(logger, task.Id, snapshot.Status.ToString(), snapshot.FinalError ?? string.Empty);
            return false;
        }

        // O card sai da fila `ready`: passa a `development` (o board_state válido que projeta o
        // estado interno `running` — ver migration 0013). Assim não é re-despachado no próximo ciclo.
        await board.MoveTaskAsync(
            new BoardTaskMoveCommand(tenantId, task.Id, "development", $"chief:{attemptId}", "agent", now),
            token);
        return true;
    }

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
