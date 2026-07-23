using Harness.Host.Profiles;
using Harness.Modules.Projects.Application;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Projects;

/// <summary>
/// CAT-07 — endpoint READ-ONLY de "metadados de atividade recente" por projeto. Deriva estritamente
/// de dados duráveis já gravados (demandas, tarefas, solicitações, tentativas, previsões),
/// humanizado e do mais recente para o mais antigo, com paginação por cursor. Tenant-scoped e
/// aditivo: não muda nenhum comportamento existente.
/// </summary>
public static class ProjectActivityEndpoints
{
    public static IEndpointRouteBuilder MapProjectActivity(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/projects/{projectId}/activity", GetActivityAsync)
            .WithTags("projects")
            .Produces<ProjectActivityPage>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GetActivityAsync(
        string projectId, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IProjectStore projects,
        ProjectActivityReadModelService service, CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return Problem(400, "invalid_project_id", "project ID must be a ULID.");
        }

        var size = limit ?? ProjectActivityProjector.DefaultLimit;
        if (size is < 1 or > ProjectActivityProjector.MaxLimit || !ProjectActivityProjector.IsValidCursor(cursor))
        {
            return Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        if (await projects.GetAsync(profile.TenantId, projectId, token) is null)
        {
            return Problem(404, "project_not_found", "The requested resource does not exist.");
        }

        var events = await service.CollectAsync(profile.TenantId, projectId, token);
        return Results.Ok(ProjectActivityProjector.Project(projectId, events, cursor, size));
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
