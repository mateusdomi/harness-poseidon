using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Agents;

/// <summary>
/// Superfície oficial do bootstrap governado de agentes (CA-5). A CLI `poseidon agent`
/// fala com estes endpoints — não existe caminho paralelo que contorne claim, conta,
/// bundle ou receipt.
/// </summary>
public static class AgentRunEndpoints
{
    public static IEndpointRouteBuilder MapAgentRuns(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var runs = endpoints.MapGroup("/api/v1/agent-runs").WithTags("agent-runs");

        runs.MapPost("/", StartAsync)
            .Produces<AgentRunResponse>(202)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);

        runs.MapGet("/{attemptId}", GetAsync)
            .Produces<AgentRunResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);

        runs.MapPost("/{attemptId}/cancel", CancelAsync)
            .Produces<AgentRunResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);

        runs.MapPost("/{attemptId}/review", ReviewAsync)
            .Produces<CriticReviewResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);

        runs.MapPost("/recovery", RecoverAsync)
            .Produces<AgentRunRecoveryResponse>()
            .ProducesProblem(401)
            .ProducesProblem(409);

        endpoints.MapGet("/api/v1/agent-accounts/doctor", DoctorAsync)
            .WithTags("agent-runs")
            .Produces<AgentAccountDoctorResponse>()
            .ProducesProblem(401)
            .ProducesProblem(409);

        // Roster REDIGIDO das identidades de execução (CA-5): apenas alias/provider/executor/
        // papéis/estado. Nunca a referência de credencial, jamais o token. Leitura pura da
        // configuração local — não executa probe, por isso não conflita (409) como o doctor.
        endpoints.MapGet("/api/v1/agent-accounts", RosterAsync)
            .WithTags("agent-runs")
            .Produces<AgentAccountRosterResponse>()
            .ProducesProblem(401)
            .ProducesProblem(409);

        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        StartAgentRunApiRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        IWorkChainStore chain,
        IClock clock,
        AgentRunSettings settings,
        IServiceProvider services,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!UlidValue.TryParse(input.ProjectId, out _))
        {
            return InvalidId("project");
        }

        if (!UlidValue.TryParse(input.TaskId, out _))
        {
            return InvalidId("task");
        }

        if (input.AttemptId is { Length: > 0 } && !UlidValue.TryParse(input.AttemptId, out _))
        {
            return InvalidId("attempt");
        }

        if (string.IsNullOrWhiteSpace(input.Instruction))
        {
            return Problem(400, "invalid_instruction", "An instruction is required.");
        }

        if (!AgentRoles.IsKnown(input.Role))
        {
            return Problem(400, "invalid_role", "The role is not part of the canonical set.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var orchestrator = services.GetService<AgentRunOrchestrator>();
        if (orchestrator is null || !settings.Enabled ||
            string.IsNullOrWhiteSpace(settings.ControlledRoot))
        {
            return Disabled();
        }

        var project = await projects.GetAsync(profile.TenantId, input.ProjectId, token);
        if (project is null)
        {
            return NotFound("project");
        }

        if (string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return Problem(
                409, "project_repository_missing",
                "The project does not declare a local repository.");
        }

        var repositoryRoot = Path.GetFullPath(project.RepositoryUrl);
        var controlledRoot = Path.GetFullPath(settings.ControlledRoot);
        if (!Directory.Exists(repositoryRoot))
        {
            return Problem(
                409, "project_repository_missing",
                "The declared project repository does not exist on this machine.");
        }

        if (!repositoryRoot.StartsWith(
                $"{controlledRoot}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return Problem(
                409, "repository_outside_controlled_root",
                "The project repository must live inside the configured controlled root.");
        }

        // A tarefa e a tentativa precisam EXISTIR: o claim durável tem chave estrangeira
        // para elas, e inventar um identificador produziria erro de banco em vez de uma
        // recusa compreensível.
        var task = await board.GetTaskAsync(profile.TenantId, input.TaskId, token);
        if (task is null)
        {
            return NotFound("task");
        }

        // RN-01 — DISCIPLINA DE CARD: nenhum trabalho de agente executa sem um CARD despachável.
        // O card precisa EXISTIR (já validado acima) E estar em estado despachável — 'agent_task',
        // com pelo menos uma instrução, não bloqueado e não arquivado. Um pedido contra um card
        // fora desse estado (bloqueado, sem instrução, tipo não-executável ou arquivado) é recusado
        // AQUI, antes de qualquer claim, conta ou execução, com código tipado e auditável. Espelha o
        // mesmo gate fail-safe (`CardReadinessEvaluator`) que o loop autônomo do Chefe aplica, para
        // que a superfície oficial da API não seja um atalho que contorne a Definition of Ready.
        var instructions = await board.ListInstructionsAsync(profile.TenantId, input.TaskId, null, 50, token);
        if (task.ArchivedAt is not null)
        {
            return Problem(
                409, "card_archived", "The card is archived and cannot be dispatched to an agent.");
        }

        var readiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
            task.CardType,
            instructions.Count >= 1,
            string.Equals(task.State, "blocked", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(task.BlockedReason)));
        if (!readiness.IsDispatchable)
        {
            return Problem(
                409, "card_not_dispatchable",
                $"The card is not in a dispatchable state: {string.Join(", ", readiness.Blockers)}.");
        }

        // A API pública não aceita ids de ferramenta fornecidos pelo cliente. Resolve a persona
        // no catálogo do servidor a partir do próprio card e só então transporta o conjunto
        // autorizado ao orquestrador. Ausência de persona é fail-closed; coleção vazia continua
        // válida quando foi declarada explicitamente por uma persona existente.
        var catalog = services.GetRequiredService<IAgentCatalogStore>();
        var personaResolution = ChiefCardResolver.Resolve(
            task.Title,
            input.Instruction,
            input.AcceptanceCriteria ?? [],
            input.RiskTier ?? "medium",
            explicitRole: input.Role);
        var definitions = await catalog.ListDefinitionsForTenantAsync(
            profile.TenantId, null, 100, false, token);
        var persona = FindExecutablePersona(definitions, personaResolution.PersonaKey)
            ?? FindExecutablePersona(definitions, personaResolution.InferredPersonaKey);
        if (persona is null)
        {
            return Problem(
                409, "persona_tools_unresolved",
                "No executable persona could be resolved for this agent run.");
        }

        // Continuação governada: recupera o artifact arquivado da tentativa reprovada e o
        // valida — checksum, receipt, review, projeto, tarefa, actor e escopo — antes de
        // permitir qualquer nova tentativa. Uma falha aqui é um código tipado, nunca um
        // best effort silencioso.
        ContinuationContext? continuation = null;
        if (input.ResumeFromAttemptId is { Length: > 0 } resumeId)
        {
            if (!UlidValue.TryParse(resumeId, out _))
            {
                return InvalidId("resume_from_attempt");
            }

            var prior = await board.GetAttemptAsync(profile.TenantId, resumeId, token);
            if (prior is null)
            {
                return NotFound("resume_attempt");
            }

            if (!string.Equals(prior.TaskId, input.TaskId, StringComparison.Ordinal))
            {
                return Problem(
                    409, "resume_attempt_task_mismatch",
                    "The attempt being resumed does not belong to the supplied task.");
            }

            var archive = services.GetService<AttemptArtifactArchive>();
            if (archive is null)
            {
                return Disabled();
            }

            var loaded = archive.TryLoad(resumeId);
            if (!loaded.Ok || loaded.Manifest is null || loaded.PatchPath is null)
            {
                return Problem(409, loaded.ReasonCode, "The archived attempt artifact could not be recovered.");
            }

            var decision = ContinuationPolicy.Evaluate(
                loaded.Manifest,
                new ContinuationPolicy.Request(
                    input.Role,
                    input.Account,
                    input.ProjectId,
                    input.TaskId,
                    repositoryRoot,
                    AgentRoles.PathScopesFor(input.Role)));
            if (!decision.Allowed)
            {
                return Problem(409, decision.ReasonCode, "The continuation is not authorized for this artifact.");
            }

            continuation = new ContinuationContext
            {
                ResumeFromAttemptId = resumeId,
                SourceCommit = loaded.Manifest.SourceCommit,
                PatchPath = loaded.PatchPath,
                PatchSha256 = loaded.Manifest.PatchSha256,
                ReviewId = loaded.Manifest.ReviewId,
                ReceiptTurnId = loaded.Manifest.ReceiptTurnId,
                PriorFindings = decision.PriorFindings,
            };
        }

        string attemptId;
        if (continuation is null && input.AttemptId is { Length: > 0 } supplied)
        {
            var attempt = await board.GetAttemptAsync(profile.TenantId, supplied, token);
            if (attempt is null)
            {
                return NotFound("attempt");
            }

            if (!string.Equals(attempt.TaskId, input.TaskId, StringComparison.Ordinal))
            {
                return Problem(
                    409, "attempt_task_mismatch",
                    "The attempt does not belong to the supplied task.");
            }

            attemptId = supplied;
        }
        else
        {
            // Sem tentativa informada, o bootstrap INICIA uma de verdade na cadeia de
            // trabalho: solicitação de origem, instrução corrente e versão esperada da
            // tarefa. O identificador nasce de um agregado durável, nunca solto. A existência
            // de instrução já foi garantida pela disciplina de card (RN-01) acima.
            attemptId = UlidValue.New(clock.UtcNow).ToString();
            var assigned = await chain.AssignTaskAsync(
                new WorkTaskAssignmentCommand(
                    profile.TenantId,
                    task.BackingSolicitationId,
                    input.TaskId,
                    instructions[^1].Id,
                    input.Account,
                    "user",
                    profile.Id,
                    $"attempt:{attemptId}",
                    task.Version,
                    $"agent-run-assignment:{attemptId}",
                    clock.UtcNow),
                token);
            if (assigned.Status != WorkChainMutationStatus.Applied &&
                (assigned.Status != WorkChainMutationStatus.IdempotentReplay ||
                    assigned.TaskState != "assigned"))
            {
                return Problem(
                    409, "task_not_assigned",
                    $"The work chain refused to assign the task ({assigned.Status}).");
            }

            var started = await chain.StartAttemptAsync(
                new WorkAttemptStartCommand(
                    profile.TenantId,
                    task.BackingSolicitationId,
                    input.TaskId,
                    instructions[^1].Id,
                    attemptId,
                    input.Account,
                    assigned.TaskVersion!.Value,
                    $"agent-run-heartbeat:{attemptId}",
                    clock.UtcNow),
                token);
            if (started.Status is not (WorkChainMutationStatus.Applied
                or WorkChainMutationStatus.IdempotentReplay))
            {
                return Problem(
                    409, "attempt_not_started",
                    $"The work chain refused to start an attempt ({started.Status}).");
            }
        }

        // O LIMITE do escopo vem do PAPEL, nunca do provider. Um pedido pode ESTREITAR para
        // sub-paths (concorrência granular entre instâncias) mas nunca AMPLIAR: a política
        // (`AgentPathScopePolicy`, avaliada pelo orquestrador) recusa qualquer claim fora do
        // papel, então um cliente não amplia o próprio escopo mandando claims extras.
        var pathScopeKind = string.Equals(
            input.Role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)
            ? AgentPathScopeKind.FrontendSpecialist
            : AgentPathScopeKind.Backend;
        var scopeClaims = input.ScopeClaims is { Count: > 0 } requestedClaims
            ? requestedClaims
            : AgentRoles.PathScopesFor(input.Role);
        if (scopeClaims.Count == 0)
        {
            return Problem(
                409, "role_has_no_path_scope",
                "This role does not own a write scope; use a read-only critic run instead.");
        }

        // Erro cedo e claro quando o pedido tenta reivindicar fora do limite do papel (ex.:
        // backend pedindo `frontend/**`). O orquestrador reavalia; aqui só antecipamos.
        var scopeDecision = AgentPathScopePolicy.Evaluate(pathScopeKind, scopeClaims);
        if (!scopeDecision.Allowed)
        {
            return Problem(
                409, scopeDecision.Code,
                $"Claims outside the role boundary: {string.Join(", ", scopeDecision.RejectedClaims)}.");
        }

        var snapshot = await orchestrator.StartAsync(
            new StartAgentRunCommand
            {
                TenantId = profile.TenantId,
                ProjectId = input.ProjectId,
                TaskId = input.TaskId,
                AttemptId = attemptId,
                Role = input.Role,
                AccountAlias = input.Account,
                Instruction = input.Instruction,
                RepositoryRoot = repositoryRoot,
                ControlledRoot = controlledRoot,
                BranchName = $"task/agent-run-{attemptId.ToLowerInvariant()}",
                WorktreePath = Path.Combine(controlledRoot, "worktrees", attemptId),
                ScopeClaims = scopeClaims,
                Owner = "agent-run-orchestrator",
                IdempotencyKey = $"agent-run:{attemptId}",
                PathScopeKind = pathScopeKind,
                Access = input.ReadOnly ? ExternalAgentAccess.ReadOnly : ExternalAgentAccess.Workspace,
                Model = input.Model,
                Effort = input.Effort,
                // A base de uma continuação é a referência interna governada, nunca um ref
                // fornecido pelo cliente: a nova worktree nasce de `origin/develop` atual.
                BaseReference = continuation is null ? "HEAD" : "origin/develop",
                RiskTier = input.RiskTier ?? "medium",
                AcceptanceCriteria = continuation is null
                    ? input.AcceptanceCriteria ?? []
                    : [.. continuation.PriorFindings, .. input.AcceptanceCriteria ?? []],
                RequiredToolIds = persona.ToolIds,
                Continuation = continuation,
            },
            token);

        return snapshot.Status switch
        {
            AgentRunStatus.Rejected => Problem(409, "agent_run_rejected", snapshot.FinalError ?? "rejected"),
            AgentRunStatus.ScopeConflict => Results.Json(ToResponse(snapshot), statusCode: 409),
            _ => Results.Json(ToResponse(snapshot), statusCode: 202),
        };
    }

    private static async Task<IResult> GetAsync(
        string attemptId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IServiceProvider services,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(attemptId, out _))
        {
            return InvalidId("attempt");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var orchestrator = services.GetService<AgentRunOrchestrator>();
        if (orchestrator is null)
        {
            return Disabled();
        }

        var snapshot = await orchestrator.GetAsync(profile.TenantId, attemptId, token);
        return snapshot is null ? NotFound("agent_run") : Results.Ok(ToResponse(snapshot));
    }

    private static async Task<IResult> CancelAsync(
        string attemptId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IServiceProvider services,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(attemptId, out _))
        {
            return InvalidId("attempt");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var orchestrator = services.GetService<AgentRunOrchestrator>();
        if (orchestrator is null)
        {
            return Disabled();
        }

        if (!await orchestrator.CancelAsync(attemptId, token))
        {
            return NotFound("agent_run");
        }

        var snapshot = await orchestrator.GetAsync(profile.TenantId, attemptId, token);
        return snapshot is null ? NotFound("agent_run") : Results.Ok(ToResponse(snapshot));
    }


    private static async Task<IResult> ReviewAsync(
        string attemptId,
        StartCriticReviewApiRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        AgentRunSettings settings,
        IServiceProvider services,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!UlidValue.TryParse(attemptId, out _))
        {
            return InvalidId("attempt");
        }

        if (string.IsNullOrWhiteSpace(input.Diff))
        {
            return Problem(400, "invalid_diff", "A diff under review is required.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var orchestrator = services.GetService<AgentRunOrchestrator>();
        if (orchestrator is null || !settings.Enabled ||
            string.IsNullOrWhiteSpace(settings.ControlledRoot))
        {
            return Disabled();
        }

        var attempt = await board.GetAttemptAsync(profile.TenantId, attemptId, token);
        if (attempt is null)
        {
            return NotFound("attempt");
        }

        var reviewDirectory = Path.GetFullPath(
            input.ReviewDirectory ?? settings.ControlledRoot);
        if (!Directory.Exists(reviewDirectory))
        {
            return Problem(
                409, "review_directory_missing",
                "The review directory does not exist on this machine.");
        }

        var result = await orchestrator.ReviewAsync(
            new AgentCriticReviewCommand
            {
                AttemptId = attemptId,
                CriticAlias = input.Critic,
                ActorAlias = input.Actor,
                ReviewDirectory = reviewDirectory,
                Diff = input.Diff,
                TestEvidence = input.TestEvidence ?? "(nenhuma evidência de teste foi fornecida)",
                AcceptanceCriteria = input.AcceptanceCriteria ?? [],
                ScopeClaims = input.ScopeClaims ?? [],
                Model = input.Model,
            },
            token);

        return Results.Ok(new CriticReviewResponse(
            result.ReviewId,
            result.AttemptId,
            result.CriticAlias,
            result.CriticExecutorId,
            result.ActorAlias,
            result.Verdict.ToString().ToLowerInvariant(),
            result.ReasonCode,
            result.Approved,
            [.. result.Findings.Select(finding => new CriticFindingContract(
                finding.Severity.ToString(), finding.Code, finding.Summary,
                finding.Path, finding.Evidence))],
            result.Summary,
            result.AccountFencingToken,
            result.DurationMs));
    }

    private static async Task<IResult> RecoverAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        IServiceProvider services,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var orchestrator = services.GetService<AgentRunOrchestrator>();
        if (orchestrator is null)
        {
            return Disabled();
        }

        return Results.Ok(new AgentRunRecoveryResponse(
            await orchestrator.RecoverAsync(profile.TenantId, token)));
    }

    private static async Task<IResult> DoctorAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        IServiceProvider services,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        var orchestrator = services.GetService<AgentRunOrchestrator>();
        if (orchestrator is null)
        {
            return Disabled();
        }

        // 0-E: o diagnóstico começa pelo PRÉ-REQUISITO. Uma conta perfeitamente configurada não
        // executa nada sem contêiner, e listar as contas como saudáveis nesse estado seria mentir
        // sobre a prontidão do produto.
        var containerRuntimeReady = Execution.ContainerRuntimeProbe.IsAvailable();
        var reports = await orchestrator.DoctorAsync(token);
        return Results.Ok(new AgentAccountDoctorResponse(
            containerRuntimeReady,
            containerRuntimeReady ? null : Execution.ContainerRuntimeProbe.Message,
            [.. reports.Select(report => new AgentAccountDoctorContract(
                report.Alias,
                report.ExecutorId,
                report.AdapterImplemented,
                report.ExecutorInstalled,
                report.DetectedVersion,
                report.ProbeReasonCode,
                report.Profile.Healthy,
                report.Profile.Findings,
                report.Authenticated))]));
    }

    private static async Task<IResult> RosterAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        AccountAvailabilityLedger availability,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        IReadOnlyList<AgentAccountDefinition> definitions;
        try
        {
            definitions = AgentAccountConfigurationLoader.LoadDefinitions(settings.AccountsFilePath);
        }
        catch (AgentAccountValidationException exception)
        {
            return Problem(409, "account_configuration_invalid", exception.Code);
        }

        return Results.Ok(RedactRoster(definitions, availability.List()));
    }

    /// <summary>
    /// Mapeia definições de conta para o contrato REDIGIDO. Este é o único ponto de saída do
    /// roster e, por construção, não pode carregar <c>CredentialRef</c> nem qualquer segredo:
    /// o contrato de resposta simplesmente não tem esse campo. Extraído como método puro para
    /// ser provado por teste sem levantar o pipeline HTTP.
    /// </summary>
    public static AgentAccountRosterResponse RedactRoster(
        IReadOnlyList<AgentAccountDefinition> definitions,
        IReadOnlyList<AccountAvailabilityRecord>? availability = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var observed = (availability ?? [])
            .ToDictionary(record => record.Alias, StringComparer.Ordinal);
        return new AgentAccountRosterResponse(
            [.. definitions.Select(definition =>
            {
                observed.TryGetValue(definition.Alias, out var current);
                var state = !definition.Enabled
                    ? "disabled"
                    : current is null
                        ? "authentication-required"
                        : PublicState(current.State);
                return new AgentAccountRosterContract(
                    definition.Alias,
                    definition.ProviderKind,
                    definition.ExecutorId,
                    definition.AllowedRoles,
                    Math.Max(1, definition.ConcurrencyLimit),
                    definition.Priority,
                    definition.Enabled,
                    state,
                    PublicHealth(current?.State, definition.Enabled),
                    current?.CooldownUntil,
                    current?.ReasonCode);
            })]);
    }

    private static string PublicState(AgentAccountState state) => state switch
    {
        AgentAccountState.Available => "idle",
        AgentAccountState.Reserved or AgentAccountState.Running => "working",
        AgentAccountState.CoolingDown => "cooldown",
        AgentAccountState.QuotaLimited => "out-of-quota",
        AgentAccountState.AuthenticationRequired => "authentication-required",
        AgentAccountState.Degraded => "degraded",
        AgentAccountState.Disabled => "disabled",
        AgentAccountState.Unavailable => "offline",
        _ => "offline",
    };

    private static string PublicHealth(AgentAccountState? state, bool enabled) =>
        !enabled || state is AgentAccountState.Disabled or AgentAccountState.Unavailable
            ? "unhealthy"
            : state is AgentAccountState.Available or AgentAccountState.Reserved or AgentAccountState.Running
                ? "healthy"
                : "attention";

    private static AgentRunResponse ToResponse(AgentRunSnapshot snapshot) =>
        new(snapshot.RunId,
            snapshot.AttemptId,
            snapshot.AccountAlias,
            snapshot.Role,
            snapshot.ExecutorId,
            snapshot.Status.ToString().ToLowerInvariant(),
            snapshot.Workspace?.BranchName,
            snapshot.Workspace?.WorktreePath,
            [.. (snapshot.Workspace?.ScopeClaims ?? []).Select(claim => claim.PathPattern)],
            snapshot.Workspace?.FencingToken,
            snapshot.AccountFencingToken,
            snapshot.SessionId,
            snapshot.ProcessId,
            snapshot.BundleChecksum,
            snapshot.ReceiptTurnId,
            [.. snapshot.Conflicts.Select(conflict => new AgentRunConflictContract(
                conflict.ExistingAttemptId, conflict.RequestedPathPattern, conflict.ExistingPathPattern))],
            snapshot.Execution is null
                ? null
                : new AgentRunExecutionContract(
                    snapshot.Execution.ExecutorId,
                    snapshot.Execution.Status.ToString().ToLowerInvariant(),
                    snapshot.Execution.FinalMessage,
                    snapshot.Execution.Usage?.InputTokens,
                    snapshot.Execution.Usage?.OutputTokens,
                    snapshot.Execution.Usage?.CostUsd,
                    snapshot.Execution.DurationMs),
            snapshot.FinalError);

    private static IResult Disabled() =>
        Problem(
            409, "agent_runs_disabled",
            "Governed agent runs are not configured for this installation.");

    private static AgentDefinitionRecord? FindExecutablePersona(
        IReadOnlyList<AgentDefinitionRecord> definitions,
        string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : definitions.FirstOrDefault(definition =>
                definition.Enabled &&
                definition.ArchivedAt is null &&
                string.Equals(definition.Role, "specialist", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(definition.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IResult InvalidId(string resource) =>
        Problem(400, $"invalid_{resource}_id", $"{resource} ID must be a ULID.");

    private static IResult SessionRequired() =>
        Problem(401, "local_session_required", "A local profile session is required.");

    private static IResult NotFound(string resource) =>
        Problem(404, $"{resource}_not_found", "The requested resource does not exist.");

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StartAgentRunApiRequest
{
    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    /// <summary>Papel lógico; define o escopo de paths.</summary>
    public required string Role { get; init; }

    /// <summary>Alias da conta. Nunca e-mail, nunca credencial.</summary>
    public required string Account { get; init; }

    public required string Instruction { get; init; }

    /// <summary>
    /// Tentativa DURÁVEL existente. Quando omitida, o bootstrap INICIA uma tentativa real
    /// para a tarefa pela cadeia de trabalho — o que é diferente de fabricar um
    /// identificador solto, que violaria a chave estrangeira do claim.
    /// </summary>
    public string? AttemptId { get; init; }

    /// <summary>
    /// Continuação governada: retoma o trabalho de uma tentativa anterior REPROVADA, pelo id
    /// durável dela. O bootstrap recupera o patch arquivado, valida checksum, receipt, review,
    /// projeto, tarefa, actor e escopo, e cria uma NOVA tentativa. Nunca é um ref Git
    /// arbitrário: a base da nova worktree é a referência interna governada
    /// (<c>origin/develop</c> atual), jamais fornecida pelo cliente.
    /// </summary>
    public string? ResumeFromAttemptId { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public string? RiskTier { get; init; }

    public bool ReadOnly { get; init; }

    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>
    /// Claims de escopo que ESTREITAM o trabalho para sub-paths dentro do limite do papel,
    /// habilitando concorrência granular (várias instâncias em subárvores disjuntas). Ausente
    /// usa o escopo padrão do papel. Nunca AMPLIA: claims fora do papel são recusados.
    /// </summary>
    public IReadOnlyList<string>? ScopeClaims { get; init; }
}

public sealed record AgentRunResponse(
    string RunId,
    string AttemptId,
    string Account,
    string Role,
    string ExecutorId,
    string Status,
    string? BranchName,
    string? WorktreePath,
    IReadOnlyList<string> ScopeClaims,
    long? WorkspaceFencingToken,
    long? AccountFencingToken,
    string? SessionId,
    int? ProcessId,
    string? BundleChecksum,
    string? ReceiptTurnId,
    IReadOnlyList<AgentRunConflictContract> Conflicts,
    AgentRunExecutionContract? Execution,
    string? FinalError);

public sealed record AgentRunConflictContract(
    string ExistingAttemptId, string RequestedPathPattern, string ExistingPathPattern);

public sealed record AgentRunExecutionContract(
    string ExecutorId,
    string Status,
    string FinalMessage,
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd,
    long DurationMs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StartCriticReviewApiRequest
{
    /// <summary>Conta do revisor. Precisa ser diferente da do actor.</summary>
    public required string Critic { get; init; }

    public required string Actor { get; init; }

    public required string Diff { get; init; }

    public string? ReviewDirectory { get; init; }

    public string? TestEvidence { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    public IReadOnlyList<string>? ScopeClaims { get; init; }
}

public sealed record CriticReviewResponse(
    string ReviewId,
    string AttemptId,
    string Critic,
    string CriticExecutorId,
    string Actor,
    string Verdict,
    string ReasonCode,
    bool Approved,
    IReadOnlyList<CriticFindingContract> Findings,
    string? Summary,
    long? AccountFencingToken,
    long DurationMs);

public sealed record CriticFindingContract(
    string Severity, string Code, string Summary, string? Path, string? Evidence);

public sealed record AgentRunRecoveryResponse(IReadOnlyList<string> Recovered);

public sealed record AgentAccountDoctorResponse(
    bool ContainerRuntimeReady,
    string? ContainerRuntimeMessage,
    IReadOnlyList<AgentAccountDoctorContract> Accounts);

public sealed record AgentAccountDoctorContract(
    string Alias,
    string ExecutorId,
    bool AdapterImplemented,
    bool ExecutorInstalled,
    string? DetectedVersion,
    string ProbeReasonCode,
    bool ProfileHealthy,
    IReadOnlyList<string> ProfileFindings,
    bool Authenticated);

public sealed record AgentAccountRosterResponse(IReadOnlyList<AgentAccountRosterContract> Accounts);

/// <summary>
/// Identidade de execução da fleet, REDIGIDA. Contém apenas o que é seguro exibir: alias,
/// provider, executor, papéis lógicos, limites e estado. NÃO existe campo de credencial ou
/// segredo neste contrato — a redação é estrutural, não um filtro que possa ser esquecido.
/// </summary>
public sealed record AgentAccountRosterContract(
    string Alias,
    string ProviderKind,
    string ExecutorId,
    IReadOnlyList<string> Roles,
    int ConcurrencyLimit,
    int Priority,
    bool Enabled,
    string State,
    string Health,
    DateTimeOffset? ReturnsAt,
    string? ReasonCode);
