using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Graph;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Graph;

namespace Harness.Host.Graph;

/// <summary>
/// A visão de PORTFÓLIO (Dual Project Gate, Parte R) — a pergunta que dois projetos simultâneos
/// criam e um projeto só nunca criou: "onde está CADA projeto, o que está travado, quem espera
/// humano?". Read model DERIVADO, lado a lado: fase, cards prontos/bloqueados e — com a flag do
/// grafo ligada — cobertura de requisitos e revalidações pendentes. Nada aqui é fonte de
/// verdade; é o Graph Phase 0 servido por HTTP, sem UI nova.
/// </summary>
public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolio(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/portfolio/situation", GetAsync)
            .WithTags("portfolio")
            .Produces<PortfolioSituationContract>()
            .ProducesProblem(401);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ProjectGraphProjectionService projection,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401, title: "local_session_required",
                detail: "A local profile session is required.");
        }

        var entries = new List<PortfolioProjectContract>();
        foreach (var project in await projects.ListAsync(profile.TenantId, null, 50, token))
        {
            var tasks = await board.ListTasksAsync(
                profile.TenantId, project.Id, null, null, 2000, token);
            var active = tasks.Where(task => task.ArchivedAt is null).ToArray();
            var phase = active
                .Where(task => !string.IsNullOrWhiteSpace(task.PhaseName))
                .OrderByDescending(task => task.UpdatedAt)
                .Select(task => task.PhaseName)
                .FirstOrDefault() ?? "(não iniciada)";

            PortfolioGraphContract? graph = null;
            if (projection.Enabled)
            {
                var snapshot = await projection.GetSnapshotAsync(profile.TenantId, project.Id, token);
                var requirements = snapshot.Nodes
                    .Where(node => node.Type == GraphNodeType.Requirement &&
                        node.State != GraphNodeState.Retired)
                    .ToArray();
                var covered = requirements.Count(requirement => snapshot.Edges.Any(edge =>
                    edge.RelationType == GraphRelationType.Implements &&
                    edge.Status == GraphEdgeStatus.Accepted &&
                    string.Equals(edge.ToNodeId, requirement.Id, StringComparison.Ordinal)));
                graph = new PortfolioGraphContract(
                    snapshot.Version,
                    requirements.Length,
                    covered,
                    snapshot.Nodes.Count(node => node.State == GraphNodeState.Stale));
            }

            entries.Add(new PortfolioProjectContract(
                project.Id,
                project.Name,
                project.State,
                project.Criticality,
                project.TargetDeadline,
                phase,
                active.Count(task => string.Equals(task.State, "ready", StringComparison.Ordinal)),
                active.Count(task =>
                    string.Equals(task.State, "blocked", StringComparison.Ordinal) ||
                    string.Equals(task.State, "escalated", StringComparison.Ordinal)),
                graph));
        }

        return Results.Ok(new PortfolioSituationContract(
            [.. entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)]));
    }
}

public sealed record PortfolioSituationContract(IReadOnlyList<PortfolioProjectContract> Projects);

public sealed record PortfolioProjectContract(
    string ProjectId,
    string Name,
    string State,
    string Criticality,
    DateTimeOffset? Deadline,
    string Phase,
    int RunnableCards,
    int BlockedCards,
    PortfolioGraphContract? Graph);

public sealed record PortfolioGraphContract(
    long GraphVersion,
    int Requirements,
    int CoveredRequirements,
    int StaleNodes);
