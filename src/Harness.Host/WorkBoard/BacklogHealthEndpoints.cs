using Harness.Host.Profiles;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

/// <summary>
/// RN-04 — SAÚDE DO BACKLOG. Read-model somente-leitura que lista os cards PRESOS (stuck): em
/// trabalho ativo além de um limite de tempo sem progresso. Consumível pelo Chefe/PO para agir —
/// a detecção NÃO auto-resolve, apenas expõe o sinal e o motivo tipado
/// (<see cref="BacklogHealthEvaluator"/>). Nenhum dado é fabricado: cada card vem do board durável.
/// </summary>
public static class BacklogHealthEndpoints
{
    /// <summary>Limite padrão sem progresso antes de um card ativo ser considerado preso.</summary>
    private const int DefaultThresholdMinutes = 120;

    /// <summary>Teto de leitura do read-model — o board de um tenant não é ilimitado.</summary>
    private const int ScanLimit = 500;

    public static IEndpointRouteBuilder MapBacklogHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var backlog = endpoints.MapGroup("/api/v1/backlog").WithTags("backlog-health");
        backlog.MapGet("/health", GetBacklogHealthAsync)
            .Produces<BacklogHealthResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            // Read-model interno do Chefe/PO. Fica FORA do contrato OpenAPI publicado nesta entrega
            // para não forçar uma regeneração do openapi; a publicação no contrato (e o consumo pelo
            // frontend) é um passo posterior. O endpoint é real, versionado e coberto por teste.
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> GetBacklogHealthAsync(
        string? projectId,
        int? thresholdMinutes,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore store,
        IClock clock,
        CancellationToken token)
    {
        if (projectId is not null && !UlidValue.TryParse(projectId, out _))
        {
            return Problem(400, "invalid_project", "Project ID must be a ULID.");
        }

        if (thresholdMinutes is < 1 or > 100_000)
        {
            return Problem(
                400, "invalid_threshold", "Threshold minutes must be between 1 and 100000.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        // Apenas cards ATIVOS (não arquivados) são candidatos a estar presos.
        var page = await store.PageTasksAsync(
            profile.TenantId,
            new BoardTaskPageQuery(
                projectId, null, null, null, null, null, "active", null, 0, ScanLimit),
            token);

        var threshold = TimeSpan.FromMinutes(thresholdMinutes ?? DefaultThresholdMinutes);
        var stuck = BacklogHealthEvaluator.Evaluate(
            page.Items.Select(task => new BacklogCardFacts(
                task.Id, task.Title, task.State, task.InternalState,
                task.ArchivedAt is not null, task.UpdatedAt)),
            clock.UtcNow,
            threshold);

        return Results.Ok(new BacklogHealthResponse(
            (long)threshold.TotalMinutes,
            page.Items.Count,
            stuck.Count,
            [.. stuck.Select(card => new StuckCardContract(
                card.TaskId, card.Title, card.ReasonCode, card.BoardState, card.InternalState,
                card.StuckForMinutes))]));
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record BacklogHealthResponse(
    long ThresholdMinutes, int ScannedCards, int StuckCards, IReadOnlyList<StuckCardContract> Stuck);

public sealed record StuckCardContract(
    string TaskId, string Title, string ReasonCode, string BoardState, string InternalState,
    long StuckForMinutes);
