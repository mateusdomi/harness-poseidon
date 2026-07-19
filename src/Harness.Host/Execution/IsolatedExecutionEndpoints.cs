using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Modules.Governance.Domain;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Execution;

public static class IsolatedExecutionEndpoints
{
    public static IEndpointRouteBuilder MapIsolatedExecutions(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/attempts/{attemptId}/isolated-executions")
            .WithTags("execution");
        group.MapPost("/", StartAsync)
            .Produces<IsolatedExecutionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        string attemptId,
        StartIsolatedExecutionApiRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        IProjectStore projects,
        IProviderCatalogStore providerCatalog,
        IAuditEventStore audit,
        IClock clock,
        IsolatedExecutionSettings settings,
        IServiceProvider services,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(attemptId, out _))
        {
            return InvalidId("attempt");
        }

        if (string.IsNullOrWhiteSpace(input.Instruction))
        {
            return Problem(400, "invalid_instruction", "An execution instruction is required.");
        }

        if (input.ScopeClaims is not { Count: > 0 })
        {
            return Problem(400, "invalid_scope_claims", "At least one scope claim is required.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return SessionRequired();
        }

        if (settings.Mode == IsolatedExecutionMode.Disabled ||
            string.IsNullOrWhiteSpace(settings.ControlledRoot))
        {
            return Problem(
                409,
                "isolated_execution_disabled",
                "Isolated execution is not configured for this installation.");
        }

        var attempt = await board.GetAttemptAsync(profile.TenantId, attemptId, token);
        if (attempt is null)
        {
            return NotFound("attempt");
        }

        var task = await board.GetTaskAsync(profile.TenantId, attempt.TaskId, token);
        if (task is null)
        {
            return NotFound("task");
        }

        var project = await projects.GetAsync(profile.TenantId, task.ProjectId, token);
        if (project is null)
        {
            return NotFound("project");
        }

        var controlledRoot = Path.GetFullPath(settings.ControlledRoot);
        if (string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return Problem(
                409,
                "project_repository_missing",
                "The project does not declare a local repository for isolated execution.");
        }

        var repositoryRoot = Path.GetFullPath(project.RepositoryUrl);
        if (!Directory.Exists(repositoryRoot))
        {
            return Problem(
                409,
                "project_repository_missing",
                "The declared project repository does not exist on this machine.");
        }

        if (!repositoryRoot.StartsWith(
                $"{controlledRoot}{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            return Problem(
                409,
                "repository_outside_controlled_root",
                "The project repository must live inside the configured controlled root.");
        }

        var budgets = await providerCatalog.ListBudgetsAsync(profile.TenantId, null, 200, token);
        var exceeded = budgets.FirstOrDefault(budget =>
            budget.LimitUsd > 0 &&
            budget.SpentUsd >= budget.LimitUsd &&
            (budget.Scope == "global" || (budget.Scope == "project" && budget.ScopeId == task.ProjectId)));
        if (exceeded is not null)
        {
            var decision = AutonomousActionGuard.Evaluate(new GuardedActionRequest(
                GuardedActionKind.BudgetOverrun,
                GuardedActorKind.System,
                project.OperationMode));
            await audit.AppendAsync(
                new AuditEventAppendCommand(
                    profile.TenantId,
                    "system",
                    null,
                    "governance.actionBlocked",
                    "attempt",
                    attemptId,
                    $"{decision.Code}: orçamento {exceeded.Id} atingiu " +
                    $"{exceeded.SpentUsd}/{exceeded.LimitUsd} USD.",
                    clock.UtcNow),
                token);
            return Problem(409, "guarded_action_blocked", decision.Detail);
        }

        var orchestrator = services.GetService<IsolatedAttemptOrchestrator>();
        if (orchestrator is null)
        {
            return Problem(
                409,
                "isolated_execution_disabled",
                "Isolated execution is not configured for this installation.");
        }

        try
        {
            var result = await orchestrator.ExecuteAsync(
                new StartIsolatedExecutionCommand
                {
                    TenantId = profile.TenantId,
                    ProjectId = task.ProjectId,
                    TaskId = task.Id,
                    AttemptId = attemptId,
                    ConversationId = attemptId,
                    AgentId = attempt.AgentId,
                    Instruction = input.Instruction.Trim(),
                    StatusDigestJson = "{}",
                    RepositoryRoot = repositoryRoot,
                    ControlledRoot = controlledRoot,
                    BaseReference = string.IsNullOrWhiteSpace(input.BaseReference)
                        ? "HEAD"
                        : input.BaseReference.Trim(),
                    BranchName = $"task/attempt-{attemptId.ToLowerInvariant()}",
                    WorktreePath = Path.Combine(controlledRoot, "worktrees", attemptId),
                    ScopeClaims = input.ScopeClaims,
                    Owner = "host-orchestrator",
                    LeaseDuration = settings.LeaseDuration,
                    IdempotencyKey = $"isolated-execution:{attemptId}",
                },
                token);
            return Results.Ok(ToResponse(result));
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_isolated_execution", exception.Message);
        }
    }

    private static IsolatedExecutionResponse ToResponse(IsolatedExecutionResult result) => new(
        result.Status switch
        {
            IsolatedExecutionStatus.Completed => "completed",
            IsolatedExecutionStatus.Failed => "failed",
            IsolatedExecutionStatus.ScopeConflict => "scopeConflict",
            IsolatedExecutionStatus.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        },
        result.Workspace is null ? null : ToContract(result.Workspace),
        result.Conflicts
            .Select(conflict => new IsolatedScopeConflictContract(
                conflict.ExistingAttemptId,
                conflict.RequestedPathPattern,
                conflict.ExistingPathPattern))
            .ToArray(),
        result.Execution is null
            ? null
            : new IsolatedExecutionRunContract(
                result.Execution.Executor,
                result.Execution.SessionId,
                result.Execution.TurnId,
                result.Execution.StructuredOutput,
                result.Execution.DurationMs),
        result.FinalError);

    private static IsolatedWorkspaceContract ToContract(AttemptWorkspaceSnapshot workspace) => new(
        workspace.AttemptId,
        workspace.TaskId,
        workspace.ProjectId,
        AttemptWorkspaceStateCodec.ToStorage(workspace.State),
        AttemptWorkspaceCleanupStateCodec.ToStorage(workspace.CleanupState),
        workspace.BranchName,
        workspace.WorktreePath,
        workspace.ScopeClaims.Select(claim => claim.PathPattern).ToArray(),
        workspace.Owner,
        workspace.FencingToken,
        workspace.CommitSha,
        workspace.SessionId,
        workspace.FinalError,
        workspace.CreatedAt,
        workspace.UpdatedAt,
        workspace.ReleasedAt,
        workspace.Version);

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
public sealed class StartIsolatedExecutionApiRequest
{
    public required string Instruction { get; init; }

    public required IReadOnlyList<string> ScopeClaims { get; init; }

    public string? BaseReference { get; init; }
}

public sealed record IsolatedExecutionResponse(
    string Status,
    IsolatedWorkspaceContract? Workspace,
    IReadOnlyList<IsolatedScopeConflictContract> Conflicts,
    IsolatedExecutionRunContract? Execution,
    string? FinalError);

public sealed record IsolatedWorkspaceContract(
    string AttemptId,
    string TaskId,
    string ProjectId,
    string State,
    string CleanupState,
    string BranchName,
    string WorktreePath,
    IReadOnlyList<string> ScopeClaims,
    string Owner,
    long FencingToken,
    string? CommitSha,
    string? SessionId,
    string? FinalError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReleasedAt,
    long Version);

public sealed record IsolatedScopeConflictContract(
    string ExistingAttemptId,
    string RequestedPathPattern,
    string ExistingPathPattern);

public sealed record IsolatedExecutionRunContract(
    string Executor,
    string SessionId,
    string TurnId,
    string StructuredOutput,
    long DurationMs);
