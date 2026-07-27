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
/// Caminho HUMANO da integração de um card aprovado. A regra e o efeito vivem em
/// <see cref="TaskIntegrationService"/>, compartilhados com o laço autônomo da chefe: o merge de
/// um card cuja revisão independente já passou não é decisão do stakeholder, e exigir o clique
/// dele card a card fazia a fábrica inteira depender de alguém estar disponível.
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
        TaskIntegrationService integration,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return InvalidId("task");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();

        // A lógica vive em TaskIntegrationService porque o mesmo merge é feito pelo laço autônomo
        // da chefe. Aqui é apenas o caminho HUMANO: mesma regra, ator diferente.
        var outcome = await integration.IntegrateAsync(profile.TenantId, id, profile.Id, token);
        if (outcome.Integrated)
        {
            return Results.Ok(new TaskMergeResult(
                id, outcome.AttemptId!, outcome.Branch!, "done", "completed"));
        }

        return outcome.ReasonCode switch
        {
            "task_not_found" => NotFound("task"),
            _ => Conflict(outcome.ReasonCode, outcome.Detail ?? outcome.ReasonCode),
        };
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
