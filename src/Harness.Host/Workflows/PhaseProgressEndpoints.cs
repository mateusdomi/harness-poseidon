using Harness.Host.Profiles;
using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Workflows;

/// <summary>
/// O progresso REAL de uma fase, para a tela. Separa três eixos que a interface vinha misturando:
/// o trabalho ACEITO (o percentual), o trabalho EM VOO (situação operacional, que nunca infla o
/// número) e a DECISÃO do portão (que é outro eixo — 100% pode coexistir com "aguardando
/// aprovação", e manter a fase em 99% por causa do humano mentiria sobre o que a equipe entregou).
/// </summary>
public static class PhaseProgressEndpoints
{
    public static IEndpointRouteBuilder MapPhaseProgress(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/workflow-runs/{runId}/phases/{phaseKey}/progress", GetAsync)
            .WithTags("workflows")
            .Produces<PhaseProgressContract>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string runId,
        string phaseKey,
        HttpRequest request,
        ILocalProfileStore profiles,
        IPhaseObligationStore obligations,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(runId, out _))
        {
            return Results.Problem(statusCode: 400, title: "invalid_run_id", detail: "ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401, title: "local_session_required",
                detail: "A local profile session is required.");
        }

        var current = await obligations.ListCurrentAsync(profile.TenantId, runId, phaseKey, token);
        if (current.Count == 0)
        {
            return Results.Problem(
                statusCode: 404, title: "phase_plan_not_found",
                detail: "The phase has no materialized obligation plan yet.");
        }

        var snapshot = PhaseProgressEvaluator.Evaluate(
        [
            .. current.Select(item => new PhaseObligation(
                item.ObligationKey,
                PhaseProgressEvaluator.ParseKind(item.Kind),
                item.Description,
                item.Required,
                (decimal)item.Weight,
                PhaseProgressEvaluator.ParseState(item.State),
                item.Source,
                item.CardId,
                item.ObjectiveKey,
                item.ArtifactRef)),
        ]);

        return Results.Ok(new PhaseProgressContract(
            runId,
            phaseKey,
            current[0].PlanVersion,
            snapshot.Percentage,
            snapshot.RequiredTotal,
            snapshot.RequiredAccepted,
            snapshot.InProgress,
            snapshot.InReview,
            snapshot.Blocked,
            snapshot.Pending,
            snapshot.OptionalTotal,
            snapshot.OptionalAccepted,
            snapshot.TechnicallyComplete,
            [
                .. current.Select(item => new PhaseObligationContract(
                    item.ObligationKey, item.Kind, item.Description, item.Required,
                    item.Weight, item.State, item.Source, item.CardId, item.ObjectiveKey,
                    item.ArtifactRef, item.Evidence, item.Reason)),
            ]));
    }
}

public sealed record PhaseObligationContract(
    string ObligationKey,
    string Kind,
    string Description,
    bool Required,
    double Weight,
    string State,
    string Source,
    string? CardId,
    string? ObjectiveKey,
    string? ArtifactRef,
    IReadOnlyList<string> Evidence,
    string? Reason);

public sealed record PhaseProgressContract(
    string RunId,
    string PhaseKey,
    int PlanVersion,
    decimal Percentage,
    int RequiredTotal,
    int RequiredAccepted,
    int InProgress,
    int InReview,
    int Blocked,
    int Pending,
    int OptionalTotal,
    int OptionalAccepted,
    bool TechnicallyComplete,
    IReadOnlyList<PhaseObligationContract> Obligations);
