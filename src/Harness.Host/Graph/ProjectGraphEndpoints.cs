using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Graph;

namespace Harness.Host.Graph;

/// <summary>
/// Endpoints da ProjectGraphProjection (Onda 1). Com a flag desligada, os dois respondem 409
/// com código estável — desligado é um ESTADO declarado, não um 404 que finge que a feature
/// não existe.
/// </summary>
public static class ProjectGraphEndpoints
{
    public static IEndpointRouteBuilder MapProjectGraph(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/projects/{projectId}/graph")
            .WithTags("project-graph");
        group.MapGet("/", GetAsync)
            .Produces<ProjectGraphSummaryContract>()
            .ProducesProblem(401)
            .ProducesProblem(409);
        group.MapPost("/rebuild", RebuildAsync)
            .Produces<ProjectGraphRebuildContract>()
            .ProducesProblem(401)
            .ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        ProjectGraphProjectionService projection,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        if (!projection.Enabled)
        {
            return Problem(
                409, "graph_projection_disabled",
                "A projeção de grafo está desligada (graph.projection.enabled=false).");
        }

        var snapshot = await projection.GetSnapshotAsync(profile.TenantId, projectId, token);
        return Results.Ok(new ProjectGraphSummaryContract(
            snapshot.Version,
            snapshot.Nodes.Count,
            snapshot.Edges.Count,
            [.. snapshot.Nodes
                .Where(node => node.State == GraphNodeState.Stale)
                .OrderBy(node => node.Id, StringComparer.Ordinal)
                .Select(node => new ProjectGraphStaleNodeContract(
                    node.Id, node.Title, node.StaleCauseNodeId, node.StaleCauseVersion))]));
    }

    private static async Task<IResult> RebuildAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        ProjectGraphProjectionService projection,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        if (!projection.Enabled)
        {
            return Problem(
                409, "graph_projection_disabled",
                "A projeção de grafo está desligada (graph.projection.enabled=false).");
        }

        var result = await projection.RebuildAsync(profile.TenantId, projectId, token);
        return Results.Ok(new ProjectGraphRebuildContract(
            result.Version, result.NodeCount, result.EdgeCount));
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record ProjectGraphSummaryContract(
    long Version,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<ProjectGraphStaleNodeContract> StaleNodes);

public sealed record ProjectGraphStaleNodeContract(
    string NodeId, string Title, string? CauseNodeId, int? CauseVersion);

public sealed record ProjectGraphRebuildContract(long Version, int NodeCount, int EdgeCount);
