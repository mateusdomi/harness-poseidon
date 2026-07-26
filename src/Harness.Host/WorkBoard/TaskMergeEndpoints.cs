using Harness.Host.Agents;
using Harness.Host.Profiles;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

/// <summary>
/// GATE HUMANO de integração: o único ponto que leva um card de `approved` (review do crítico) a
/// `done`. Faz o merge REAL (--no-ff) da branch da tentativa aprovada na referência publicada do
/// repositório do projeto e persiste as duas transições da cadeia durável
/// (<c>MergeApprovedTaskAsync</c> → <c>CompleteMergedTaskAsync</c>), idempotentes por tentativa.
/// Nunca é automático: exige uma sessão humana. É esta conclusão (`done`) que libera a próxima
/// onda de cards do plano na triagem do chefe.
/// </summary>
public static class TaskMergeEndpoints
{
    public static IEndpointRouteBuilder MapTaskMerge(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/tasks/{id}/merge", MergeAsync)
            .WithTags("tasks")
            .Produces<TaskMergeResult>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> MergeAsync(
        string id,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        IWorkChainStore chain,
        IProjectStore projects,
        AgentRunSettings settings,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId("task");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        var task = await board.GetTaskAsync(profile.TenantId, id, token);
        if (task is null) return NotFound("task");
        if (!string.Equals(task.InternalState, "approved", StringComparison.Ordinal))
        {
            return Conflict(
                "task_not_approved",
                $"Only tasks approved by the independent review can be merged (state: {task.InternalState}).");
        }

        // O estado exposto do attempt é o OPERACIONAL: a tentativa aprovada fica 'completed'
        // (a reprovada fica 'failed'); a autoridade da fase é o estado interno do card, já
        // exigido acima ('approved') — e a cadeia revalida ao persistir o merge.
        var attempts = await board.ListAttemptsAsync(profile.TenantId, task.Id, null, 100, token);
        var approved = attempts.LastOrDefault(attempt =>
            string.Equals(attempt.State, "completed", StringComparison.Ordinal));
        if (approved is null) return Conflict("approved_attempt_missing", "The task has no approved attempt.");

        var project = await projects.GetAsync(profile.TenantId, task.ProjectId, token);
        if (project is null || string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return Conflict("project_repository_missing", "The project has no local repository bound.");
        }

        if (string.IsNullOrWhiteSpace(settings.ControlledRoot))
        {
            return Conflict(
                "merge_unavailable",
                "Merging requires Harness:AgentRuns:ControlledRoot on this machine.");
        }

        // 1. Merge git REAL da branch da tentativa. Conflito aborta o merge e devolve 409 —
        //    o repositório nunca fica no meio de um merge.
        var branch = $"task/agent-run-{approved.Id.ToLowerInvariant()}";
        try
        {
            using var manager = await GitWorktreeManager.OpenAsync(
                System.IO.Path.GetFullPath(project.RepositoryUrl),
                System.IO.Path.GetFullPath(settings.ControlledRoot),
                token);
            await manager.MergeTaskBranchAsync(
                branch,
                $"merge(card {DisplayCode(task.Title)}): tentativa {approved.Id} aprovada em review",
                token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Conflict("merge_failed", exception.Message);
        }

        // 2. Cadeia durável: approved → merged → completed (board `done`), idempotente por
        //    tentativa. A conclusão destrava a próxima onda do plano na triagem do chefe.
        var now = clock.UtcNow;
        var merged = await chain.MergeApprovedTaskAsync(
            new WorkTaskMergeCommand(
                profile.TenantId, task.BackingSolicitationId, task.Id, profile.Id,
                $"git-branch:{branch}", task.Version, $"task-merge:{approved.Id}", now),
            token);
        if (merged.Status is not (WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay))
        {
            return Conflict("merge_state_conflict", $"The work chain rejected the merge: {merged.Status}.");
        }

        var completed = await chain.CompleteMergedTaskAsync(
            new WorkTaskDeliveryCompleteCommand(
                profile.TenantId, task.BackingSolicitationId, task.Id, profile.Id,
                $"git-merge:{branch}", merged.TaskVersion ?? task.Version + 1,
                $"task-merge-complete:{approved.Id}", clock.UtcNow),
            token);
        if (completed.Status is not (WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay))
        {
            return Conflict("merge_completion_conflict", $"The work chain rejected the completion: {completed.Status}.");
        }

        var refreshed = await board.GetTaskAsync(profile.TenantId, task.Id, token);
        return Results.Ok(new TaskMergeResult(
            task.Id, approved.Id, branch, refreshed?.State ?? "done",
            refreshed?.InternalState ?? "completed"));
    }

    private static string DisplayCode(string title)
    {
        var space = title.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? title : title[..space];
    }

    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", "ID must be a ULID.");
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist.");
    private static IResult Conflict(string title, string detail) => Problem(409, title, detail);
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

/// <summary>Desfecho do gate humano: card, tentativa integrada, branch e estados resultantes.</summary>
public sealed record TaskMergeResult(
    string TaskId,
    string AttemptId,
    string Branch,
    string BoardState,
    string InternalState);
