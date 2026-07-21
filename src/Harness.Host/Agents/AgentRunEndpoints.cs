using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;

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

        runs.MapPost("/recovery", RecoverAsync)
            .Produces<AgentRunRecoveryResponse>()
            .ProducesProblem(401)
            .ProducesProblem(409);

        endpoints.MapGet("/api/v1/agent-accounts/doctor", DoctorAsync)
            .WithTags("agent-runs")
            .Produces<AgentAccountDoctorResponse>()
            .ProducesProblem(401)
            .ProducesProblem(409);

        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        StartAgentRunApiRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
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

        var attemptId = input.AttemptId is { Length: > 0 } supplied
            ? supplied
            : UlidValue.New(DateTimeOffset.UtcNow).ToString();

        // O escopo vem do PAPEL, nunca do provider nem do pedido: um cliente não amplia o
        // próprio escopo mandando claims extras.
        var scopeClaims = AgentRoles.PathScopesFor(input.Role);
        var pathScopeKind = string.Equals(
            input.Role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)
            ? AgentPathScopeKind.FrontendSpecialist
            : AgentPathScopeKind.Backend;
        if (scopeClaims.Count == 0)
        {
            return Problem(
                409, "role_has_no_path_scope",
                "This role does not own a write scope; use a read-only critic run instead.");
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
                RiskTier = input.RiskTier ?? "medium",
                AcceptanceCriteria = input.AcceptanceCriteria ?? [],
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

        var reports = await orchestrator.DoctorAsync(token);
        return Results.Ok(new AgentAccountDoctorResponse(
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

    public string? AttemptId { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public string? RiskTier { get; init; }

    public bool ReadOnly { get; init; }

    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }
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

public sealed record AgentRunRecoveryResponse(IReadOnlyList<string> Recovered);

public sealed record AgentAccountDoctorResponse(IReadOnlyList<AgentAccountDoctorContract> Accounts);

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
