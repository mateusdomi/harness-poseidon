using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Governance;

public static partial class GovernanceEndpoints
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEndpointRouteBuilder MapGovernance(this IEndpointRouteBuilder endpoints)
    {
        var audit = endpoints.MapGroup("/api/v1/audit-events").WithTags("governance");
        audit.MapGet("/", ListAsync).Produces<AuditEventPage>().ProducesProblem(400).ProducesProblem(401);
        audit.MapGet("/integrity", IntegrityAsync).Produces<AuditIntegrityContract>().ProducesProblem(401);
        audit.MapGet("/export", ExportAsync).Produces(200, contentType: "application/json").Produces(200, contentType: "text/csv").ProducesProblem(400).ProducesProblem(401);
        audit.MapGet("/{id}", GetAsync).Produces<AuditEventContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(string? cursor, int? limit, string? actorKind, string? actorId, string? action, string? targetType, string? targetId, DateTimeOffset? from, DateTimeOffset? to, HttpRequest request, ILocalProfileStore profiles, IAuditEventStore store, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized();
        var validation = Validate(cursor, limit, actorKind, actorId, action, targetType, targetId, from, to); if (validation is not null) return validation;
        var size = limit ?? 200; var rows = await store.ListAsync(new(session.TenantId, cursor, size + 1, actorKind, actorId, action, targetType, targetId, from, to), token); var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new AuditEventPage(items, rows.Count > size ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAsync(string id, HttpRequest request, ILocalProfileStore profiles, IAuditEventStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return Invalid("invalid_audit_event_id", "Audit event ID must be a ULID."); var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized();
        var value = await store.GetAsync(session.TenantId, id, token); return value is null ? Results.Problem(statusCode: 404, title: "audit_event_not_found", detail: "The audit event does not exist.") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> IntegrityAsync(HttpRequest request, ILocalProfileStore profiles, IAuditEventStore store, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized(); var value = await store.VerifyIntegrityAsync(session.TenantId, token);
        return Results.Ok(new AuditIntegrityContract(value.Valid, value.EntryCount, value.LastSequence, value.TailHash, value.FailedSequence));
    }

    private static async Task<IResult> ExportAsync(string? format, string? actorKind, string? actorId, string? action, string? targetType, string? targetId, DateTimeOffset? from, DateTimeOffset? to, HttpRequest request, ILocalProfileStore profiles, IAuditEventStore store, CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token); if (session is null) return Unauthorized(); format ??= "json";
        if (format is not ("json" or "csv")) return Invalid("invalid_audit_export_format", "Export format must be json or csv.");
        var validation = Validate(null, null, actorKind, actorId, action, targetType, targetId, from, to); if (validation is not null) return validation;
        var items = (await store.ListAsync(new(session.TenantId, null, int.MaxValue, actorKind, actorId, action, targetType, targetId, from, to), token)).Select(ToContract).Select(Mask).ToArray();
        if (format == "csv") return Results.File(Encoding.UTF8.GetBytes(ToCsv(items)), "text/csv; charset=utf-8", "poseidon-auditoria.csv");
        return Results.File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items, ExportJsonOptions)), "application/json; charset=utf-8", "poseidon-auditoria.json");
    }

    private static IResult? Validate(string? cursor, int? limit, string? actorKind, string? actorId, string? action, string? targetType, string? targetId, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (cursor is not null && !UlidValue.TryParse(cursor, out _) || limit is < 1 or > 200 || actorKind is not null && actorKind is not ("user" or "chief" or "agent" or "system") || actorId is not null && !UlidValue.TryParse(actorId, out _) || targetId is not null && !UlidValue.TryParse(targetId, out _) || string.IsNullOrWhiteSpace(action) && action is not null || string.IsNullOrWhiteSpace(targetType) && targetType is not null || from > to)
            return Invalid("invalid_audit_query", "Audit query is invalid.");
        return null;
    }

    private static AuditEventContract ToContract(AuditEventRecord value) => new(value.Id, value.ActorKind, value.ActorId, value.Action, value.TargetType, value.TargetId, value.Detail, value.OccurredAt);
    private static AuditEventContract Mask(AuditEventContract value) => value with { Detail = value.Detail is null ? null : BareSecretPattern().Replace(NamedSecretPattern().Replace(value.Detail, "$1=****"), "****") };
    private static string ToCsv(IEnumerable<AuditEventContract> items)
    {
        var rows = new List<string> { "id,occurredAt,actorKind,actorId,action,targetType,targetId,detail" };
        rows.AddRange(items.Select(item => string.Join(',', Cell(item.Id), Cell(item.OccurredAt.ToString("O")), Cell(item.ActorKind), Cell(item.ActorId), Cell(item.Action), Cell(item.TargetType), Cell(item.TargetId), Cell(item.Detail)))); return string.Join('\n', rows);
    }
    private static string Cell(string? value) { if (value is null) return string.Empty; return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""; }
    private static IResult Unauthorized() => Results.Problem(statusCode: 401, title: "local_session_required", detail: "A local profile session is required.");
    private static IResult Invalid(string title, string detail) => Results.Problem(statusCode: 400, title: title, detail: detail);

    [GeneratedRegex(@"(?i)\b(token|api[_-]?key|secret|password)\s*[:=]\s*([^\s,;]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NamedSecretPattern();
    [GeneratedRegex(@"(?i)\b(sk|rk|pk)-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BareSecretPattern();
}

public sealed record AuditEventContract(string Id, string ActorKind, string? ActorId, string Action, string TargetType, string? TargetId, string? Detail, DateTimeOffset OccurredAt);
public sealed record AuditEventPage(IReadOnlyList<AuditEventContract> Items, string? NextCursor);
public sealed record AuditIntegrityContract(bool Valid, long EntryCount, long LastSequence, string TailHash, long? FailedSequence);
