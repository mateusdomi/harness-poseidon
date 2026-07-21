using Harness.Host.Profiles;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Readiness;

public static class ReadinessEndpoints
{
    public static IEndpointRouteBuilder MapReadiness(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/v1/projects/{projectId}").WithTags("readiness");
        group.MapGet("/readiness", GetAsync)
            .Produces<ProjectReadinessSnapshot>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projectStore,
        ProjectReadinessService readiness,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var project = await projectStore.GetAsync(profile.TenantId, projectId, cancellationToken);
        if (project is null)
        {
            return Problem(404, "project_not_found", "The project does not exist.");
        }

        var snapshot = await readiness.EvaluateAsync(
            profile.TenantId, project, profileReady: true, cancellationToken);
        return Results.Ok(snapshot);
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
