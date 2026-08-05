using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Attention;
using Harness.Persistence.Abstractions.Identity;

namespace Harness.Host.Notifications;

/// <summary>
/// A superfície HTTP do Human Attention Loop: "o que está parado esperando o humano?" —
/// por projeto, com escopo de bloqueio, estado do canal e idade. Ack marca que a pessoa VIU
/// (pára os lembretes agressivos); Answer registra a decisão e libera o subgrafo dependente.
/// </summary>
public static class HumanAttentionEndpoints
{
    public static IEndpointRouteBuilder MapHumanAttention(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/attention").WithTags("human-attention");
        group.MapGet("/", ListAsync)
            .Produces<HumanAttentionListContract>()
            .ProducesProblem(401);
        group.MapPost("/{id}/acknowledge", AcknowledgeAsync)
            .Produces(204)
            .ProducesProblem(401);
        group.MapPost("/{id}/answer", AnswerAsync)
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(401);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        Harness.SharedKernel.Time.IClock clock,
        CancellationToken token)
    {
        var attention = request.HttpContext.RequestServices
            .GetService<IHumanAttentionStore>();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(
                statusCode: 401, title: "local_session_required",
                detail: "A local profile session is required.");
        }

        if (attention is null)
        {
            return Results.Problem(
                statusCode: 409, title: "attention_store_unavailable",
                detail: "A store de atenção humana não está disponível neste provider.");
        }

        var projectId = request.Query.TryGetValue("projectId", out var value)
            ? value.ToString()
            : null;
        var open = await attention.ListOpenAsync(
            profile.TenantId, string.IsNullOrWhiteSpace(projectId) ? null : projectId, token);
        return Results.Ok(new HumanAttentionListContract(
            [.. open.Select(item => new HumanAttentionContract(
                item.Id, item.ProjectId, item.Question, item.Reason, item.Severity,
                item.BlockingScope, item.Status, item.CardId, item.GraphNodeId,
                item.CreatedAt, item.ReminderCount, item.ChannelStatus,
                (clock.UtcNow - item.CreatedAt).TotalMinutes))]));
    }

    private static async Task<IResult> AcknowledgeAsync(
        string id,
        HttpRequest request,
        ILocalProfileStore profiles,
        Harness.SharedKernel.Time.IClock clock,
        CancellationToken token)
    {
        var attention = request.HttpContext.RequestServices
            .GetService<IHumanAttentionStore>();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(statusCode: 401, title: "local_session_required");
        }

        if (attention is null)
        {
            return Results.Problem(statusCode: 409, title: "attention_store_unavailable");
        }

        await attention.AcknowledgeAsync(profile.TenantId, id, clock.UtcNow, token);
        return Results.NoContent();
    }

    private static async Task<IResult> AnswerAsync(
        string id,
        HumanAttentionAnswerContract input,
        HttpRequest request,
        ILocalProfileStore profiles,
        Harness.SharedKernel.Time.IClock clock,
        CancellationToken token)
    {
        var attention = request.HttpContext.RequestServices
            .GetService<IHumanAttentionStore>();
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Results.Problem(statusCode: 401, title: "local_session_required");
        }

        if (string.IsNullOrWhiteSpace(input.Answer))
        {
            return Results.Problem(
                statusCode: 400, title: "answer_required",
                detail: "A resposta não pode ser vazia.");
        }

        if (attention is null)
        {
            return Results.Problem(statusCode: 409, title: "attention_store_unavailable");
        }

        await attention.AnswerAsync(profile.TenantId, id, input.Answer.Trim(), clock.UtcNow, token);
        return Results.NoContent();
    }
}

public sealed record HumanAttentionListContract(IReadOnlyList<HumanAttentionContract> Requests);

public sealed record HumanAttentionContract(
    string Id,
    string ProjectId,
    string Question,
    string Reason,
    string Severity,
    string BlockingScope,
    string Status,
    string? CardId,
    string? GraphNodeId,
    DateTimeOffset CreatedAt,
    int ReminderCount,
    string ChannelStatus,
    double AgeMinutes);

public sealed record HumanAttentionAnswerContract(string Answer);
