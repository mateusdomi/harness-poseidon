using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Operations;

public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapLocalOperations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/backups", CreateBackupAsync).WithTags("backups")
            .Produces<BackupHandle>(201).ProducesProblem(401).ProducesProblem(409);
        endpoints.MapPost("/api/v1/backups/{id}/restore", RestoreBackupAsync).WithTags("backups")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        endpoints.MapGet("/api/v1/diagnostics", GetDiagnosticsAsync).WithTags("diagnostics")
            .Produces<DiagnosticsContract>().ProducesProblem(401);
        return endpoints;
    }

    private static async Task<IResult> CreateBackupAsync(
        HttpRequest request, ILocalProfileStore profiles, LocalOperationsService operations,
        IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return Unauthorized();
        try
        {
            var now = clock.UtcNow; var id = UlidValue.New(now).ToString();
            var handle = await operations.CreateBackupAsync(profile.TenantId, profile.Id, id, now, token);
            return Results.Created($"/api/v1/backups/{id}", handle);
        }
        catch (InvalidOperationException e) { return Problem(409, "backup_conflict", e.Message); }
    }

    private static async Task<IResult> RestoreBackupAsync(
        string id, HttpRequest request, ILocalProfileStore profiles,
        LocalOperationsService operations, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return Problem(400, "invalid_backup_id", "Backup ID must be a ULID.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return Unauthorized();
        try
        {
            await operations.RestoreBackupAsync(profile.TenantId, profile.Id, id, clock.UtcNow, token);
            return Results.NoContent();
        }
        catch (LocalBackupNotFoundException) { return Problem(404, "backup_not_found", "The backup does not exist."); }
        catch (InvalidOperationException e) { return Problem(409, "backup_restore_conflict", e.Message); }
    }

    private static async Task<IResult> GetDiagnosticsAsync(
        HttpRequest request, ILocalProfileStore profiles, LocalOperationsService operations,
        IClock clock, CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return Unauthorized();
        var checks = await operations.DiagnoseAsync(token);
        return Results.Ok(new DiagnosticsContract(new("Poseidon", "0.3.0", "Harness"),
            "http", "available", checks, clock.UtcNow));
    }

    private static IResult Unauthorized() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}
