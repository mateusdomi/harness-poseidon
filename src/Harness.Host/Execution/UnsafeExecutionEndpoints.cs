using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Execution;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Time;

namespace Harness.Host.Execution;

/// <summary>
/// Fase 0B1 (BR-002): a ÚNICA porta do modo inseguro.
///
/// A política do bloco é explícita: sem sandbox atestada, ferramenta crítica é negada; o modo
/// inseguro só existe mediante aceite do proprietário; e ele NUNCA pode ser ativado por um agente.
/// Por isso o aceite vive atrás de uma sessão de perfil local — a mesma credencial do dono no
/// produto — e não existe nenhum caminho programático, variável de ambiente ou configuração que o
/// ligue. Um agente que quisesse se autoautorizar precisaria da sessão do humano.
///
/// O aceite tem motivo obrigatório, autor, validade e auditoria. Aceitar risco sem registro é o
/// mesmo que não ter política.
/// </summary>
public static class UnsafeExecutionEndpoints
{
    /// <summary>Teto de validade do aceite: risco aceito não vira permanente por esquecimento.</summary>
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromHours(24);

    public static IEndpointRouteBuilder MapUnsafeExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/v1/projects/{projectId}/unsafe-execution", GetAsync);
        app.MapPost("/api/v1/projects/{projectId}/unsafe-execution", AcceptAsync);
        app.MapDelete("/api/v1/projects/{projectId}/unsafe-execution", RevokeAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(
        string projectId, HttpRequest request, ILocalProfileStore profiles,
        ISandboxAttestationStore store, IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var acceptance = await store.GetUnsafeAcceptanceAsync(
            profile.TenantId, projectId, clock.UtcNow, token);
        return Results.Ok(new UnsafeExecutionStatus(
            acceptance is not null,
            acceptance?.AcceptedByProfileId,
            acceptance?.Reason,
            acceptance?.AcceptedAt,
            acceptance?.ExpiresAt));
    }

    private static async Task<IResult> AcceptAsync(
        string projectId, UnsafeExecutionAcceptRequest? input, HttpRequest request,
        ILocalProfileStore profiles, ISandboxAttestationStore store, IAuditEventStore audit,
        IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var reason = input?.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length is < 10 or > 2_000)
        {
            // Motivo é obrigatório e precisa dizer alguma coisa: "ok" não é aceite de risco, é
            // clique. Quem ler a auditoria depois precisa entender o que foi aceito e por quê.
            return Problem(
                400,
                "unsafe_reason_required",
                "Accepting unsafe execution requires a reason between 10 and 2000 characters.");
        }

        var requested = input?.Duration ?? TimeSpan.FromHours(1);
        if (requested <= TimeSpan.Zero || requested > MaximumWindow)
        {
            return Problem(
                400,
                "unsafe_duration_invalid",
                "The unsafe-execution window must be positive and no longer than 24 hours.");
        }

        var now = clock.UtcNow;
        var record = await store.AcceptUnsafeAsync(
            new UnsafeExecutionAcceptanceRecord(
                profile.TenantId, projectId, profile.Id, reason, now, now.Add(requested)),
            token);
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId, "user", profile.Id, "execution.unsafeModeAccepted", "project",
                projectId, reason, now),
            token);
        return Results.Ok(new UnsafeExecutionStatus(
            true, record.AcceptedByProfileId, record.Reason, record.AcceptedAt, record.ExpiresAt));
    }

    private static async Task<IResult> RevokeAsync(
        string projectId, HttpRequest request, ILocalProfileStore profiles,
        ISandboxAttestationStore store, IAuditEventStore audit, IClock clock,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var now = clock.UtcNow;
        var revoked = await store.RevokeUnsafeAsync(profile.TenantId, projectId, now, token);
        if (revoked)
        {
            await audit.AppendAsync(
                new AuditEventAppendCommand(
                    profile.TenantId, "user", profile.Id, "execution.unsafeModeRevoked", "project",
                    projectId, "The owner revoked the unsafe-execution acceptance.", now),
                token);
        }

        return Results.Ok(new UnsafeExecutionStatus(false, null, null, null, null));
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record UnsafeExecutionAcceptRequest(string? Reason, TimeSpan? Duration);

public sealed record UnsafeExecutionStatus(
    bool Accepted,
    string? AcceptedByProfileId,
    string? Reason,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? ExpiresAt);
