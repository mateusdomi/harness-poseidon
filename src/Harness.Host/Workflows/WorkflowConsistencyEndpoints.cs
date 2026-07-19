using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

public static class WorkflowConsistencyEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowConsistency(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/workflow-runs/{id}/consistency-checks",
                CheckAsync)
            .WithTags("workflow-runs")
            .Produces<WorkflowConsistencyCheckResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> CheckAsync(
        string id,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkflowStore authority,
        IWorkflowCatalogStore catalog,
        IWorkflowConsistencyReviewer reviewer,
        IAuditEventStore audit,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _))
        {
            return Results.Problem(
                statusCode: 400,
                title: "invalid_run_id",
                detail: "Run ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401,
                title: "local_session_required",
                detail: "A local profile session is required.");
        }

        var run = await authority.ReadRunAggregateAsync(profile.TenantId, id, token);
        if (run is null)
        {
            return Results.Problem(
                statusCode: 404,
                title: "workflow_run_not_found",
                detail: "The requested resource does not exist.");
        }

        var projectedRun = await catalog.GetRunAsync(profile.TenantId, id, token);
        var projectedPhases = await catalog.ListPhasesAsync(profile.TenantId, id, null, 200, token);
        var projectedGates = await catalog.ListGatesAsync(profile.TenantId, id, null, 200, token);
        var report = await WorkflowConsistencyVerifier.VerifyAsync(
            run,
            projectedRun,
            projectedPhases,
            projectedGates,
            reviewer,
            token);
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "workflow.consistencyVerified",
                "workflow_run",
                id,
                $"consistent={report.Consistent}; " + string.Join(
                    "; ",
                    report.Layers.Select(layer =>
                        $"{layer.Layer}={StateName(layer.State)}({layer.Findings.Count})")),
                clock.UtcNow),
            token);
        return Results.Ok(new WorkflowConsistencyCheckResponse(
            report.RunId,
            report.Consistent,
            report.Layers
                .Select(layer => new ConsistencyLayerContract(
                    layer.Layer,
                    StateName(layer.State),
                    layer.Findings
                        .Select(finding => new ConsistencyFindingContract(finding.Code, finding.Detail))
                        .ToArray()))
                .ToArray()));
    }

    private static string StateName(ConsistencyLayerState state) => state switch
    {
        ConsistencyLayerState.Passed => "passed",
        ConsistencyLayerState.Failed => "failed",
        ConsistencyLayerState.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown layer state."),
    };
}

public sealed record WorkflowConsistencyCheckResponse(
    string RunId,
    bool Consistent,
    IReadOnlyList<ConsistencyLayerContract> Layers);

public sealed record ConsistencyLayerContract(
    string Layer,
    string State,
    IReadOnlyList<ConsistencyFindingContract> Findings);

public sealed record ConsistencyFindingContract(string Code, string Detail);
