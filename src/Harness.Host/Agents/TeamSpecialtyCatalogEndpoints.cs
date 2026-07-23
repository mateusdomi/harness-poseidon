using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Agents;

/// <summary>
/// CRUD tenant-scoped dos catálogos reais de equipe (team) e especialidade (specialty).
/// Uma especialidade pode, opcionalmente, pertencer a uma equipe; apagar uma entrada ainda
/// referenciada devolve um 409 tipado.
/// </summary>
public static class TeamSpecialtyCatalogEndpoints
{
    public static IEndpointRouteBuilder MapTeamSpecialtyCatalog(this IEndpointRouteBuilder endpoints)
    {
        var teams = endpoints.MapGroup("/api/v1/teams").WithTags("agents");
        teams.MapGet("/", ListTeamsAsync).Produces<TeamPage>().ProducesProblem(400).ProducesProblem(401);
        teams.MapGet("/{teamId}", GetTeamAsync).Produces<TeamContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        teams.MapPost("/", CreateTeamAsync).Produces<TeamContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        teams.MapPatch("/{teamId}", UpdateTeamAsync).Produces<TeamContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        teams.MapDelete("/{teamId}", DeleteTeamAsync).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

        var specialties = endpoints.MapGroup("/api/v1/specialties").WithTags("agents");
        specialties.MapGet("/", ListSpecialtiesAsync).Produces<SpecialtyPage>().ProducesProblem(400).ProducesProblem(401);
        specialties.MapGet("/{specialtyId}", GetSpecialtyAsync).Produces<SpecialtyContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        specialties.MapPost("/", CreateSpecialtyAsync).Produces<SpecialtyContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        specialties.MapPatch("/{specialtyId}", UpdateSpecialtyAsync).Produces<SpecialtyContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        specialties.MapDelete("/{specialtyId}", DeleteSpecialtyAsync).Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> ListTeamsAsync(
        string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (!TryPage(cursor, limit, out var size)) return InvalidCursor();
        var values = await store.ListTeamsAsync(profile.TenantId, cursor, size + 1, token);
        var more = values.Count > size;
        var items = values.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new TeamPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetTeamAsync(
        string teamId, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(teamId, out _)) return InvalidId("team");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var value = await store.GetTeamAsync(profile.TenantId, teamId, token);
        return value is null ? NotFound("team") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> CreateTeamAsync(
        TeamWriteRequest input, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var now = clock.UtcNow;
        var id = UlidValue.New(now).ToString();
        try
        {
            var value = await store.CreateTeamAsync(new(profile.TenantId, profile.Id, id, input.Key, input.Name, input.Description, now), token);
            return Results.Created($"/api/v1/teams/{id}", ToContract(value));
        }
        catch (TeamSpecialtyCatalogValidationException e) { return Problem(400, "invalid_team", e.Message); }
        catch (TeamSpecialtyCatalogConflictException e) { return Problem(409, "team_conflict", e.Message); }
    }

    private static async Task<IResult> UpdateTeamAsync(
        string teamId, TeamWriteRequest input, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(teamId, out _)) return InvalidId("team");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var value = await store.UpdateTeamAsync(new(profile.TenantId, profile.Id, teamId, input.Key, input.Name, input.Description, clock.UtcNow), token);
            return Results.Ok(ToContract(value));
        }
        catch (TeamSpecialtyCatalogNotFoundException) { return NotFound("team"); }
        catch (TeamSpecialtyCatalogValidationException e) { return Problem(400, "invalid_team", e.Message); }
        catch (TeamSpecialtyCatalogConflictException e) { return Problem(409, "team_conflict", e.Message); }
    }

    private static async Task<IResult> DeleteTeamAsync(
        string teamId, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(teamId, out _)) return InvalidId("team");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            await store.DeleteTeamAsync(new(profile.TenantId, profile.Id, teamId, clock.UtcNow), token);
            return Results.NoContent();
        }
        catch (TeamSpecialtyCatalogNotFoundException) { return NotFound("team"); }
        catch (TeamSpecialtyCatalogConflictException e) { return Problem(409, "team_conflict", e.Message); }
    }

    private static async Task<IResult> ListSpecialtiesAsync(
        string? teamId, string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, CancellationToken token)
    {
        if (teamId is not null && !UlidValue.TryParse(teamId, out _)) return InvalidId("team");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (!TryPage(cursor, limit, out var size)) return InvalidCursor();
        var values = await store.ListSpecialtiesAsync(profile.TenantId, teamId, cursor, size + 1, token);
        var more = values.Count > size;
        var items = values.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new SpecialtyPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetSpecialtyAsync(
        string specialtyId, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(specialtyId, out _)) return InvalidId("specialty");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var value = await store.GetSpecialtyAsync(profile.TenantId, specialtyId, token);
        return value is null ? NotFound("specialty") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> CreateSpecialtyAsync(
        SpecialtyWriteRequest input, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, IClock clock, CancellationToken token)
    {
        if (input.TeamId is not null && !UlidValue.TryParse(input.TeamId, out _)) return InvalidId("team");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var now = clock.UtcNow;
        var id = UlidValue.New(now).ToString();
        try
        {
            var value = await store.CreateSpecialtyAsync(new(profile.TenantId, profile.Id, id, input.Key, input.Name, input.Description, input.TeamId, now), token);
            return Results.Created($"/api/v1/specialties/{id}", ToContract(value));
        }
        catch (TeamSpecialtyCatalogValidationException e) { return Problem(400, "invalid_specialty", e.Message); }
        catch (TeamSpecialtyCatalogConflictException e) { return Problem(409, "specialty_conflict", e.Message); }
    }

    private static async Task<IResult> UpdateSpecialtyAsync(
        string specialtyId, SpecialtyWriteRequest input, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(specialtyId, out _)) return InvalidId("specialty");
        if (input.TeamId is not null && !UlidValue.TryParse(input.TeamId, out _)) return InvalidId("team");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var value = await store.UpdateSpecialtyAsync(new(profile.TenantId, profile.Id, specialtyId, input.Key, input.Name, input.Description, input.TeamId, clock.UtcNow), token);
            return Results.Ok(ToContract(value));
        }
        catch (TeamSpecialtyCatalogNotFoundException) { return NotFound("specialty"); }
        catch (TeamSpecialtyCatalogValidationException e) { return Problem(400, "invalid_specialty", e.Message); }
        catch (TeamSpecialtyCatalogConflictException e) { return Problem(409, "specialty_conflict", e.Message); }
    }

    private static async Task<IResult> DeleteSpecialtyAsync(
        string specialtyId, HttpRequest request, ILocalProfileStore profiles,
        ITeamSpecialtyCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(specialtyId, out _)) return InvalidId("specialty");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            await store.DeleteSpecialtyAsync(new(profile.TenantId, profile.Id, specialtyId, clock.UtcNow), token);
            return Results.NoContent();
        }
        catch (TeamSpecialtyCatalogNotFoundException) { return NotFound("specialty"); }
        catch (TeamSpecialtyCatalogConflictException e) { return Problem(409, "specialty_conflict", e.Message); }
    }

    private static bool TryPage(string? cursor, int? limit, out int size)
    {
        size = limit ?? 50;
        return size is >= 1 and <= 200 && (cursor is null || UlidValue.TryParse(cursor, out _));
    }

    private static TeamContract ToContract(TeamRecord value) =>
        new(value.Id, value.Key, value.Name, value.Description, value.CreatedAt, value.UpdatedAt);

    private static SpecialtyContract ToContract(SpecialtyRecord value) =>
        new(value.Id, value.Key, value.Name, value.Description, value.TeamId, value.CreatedAt, value.UpdatedAt);

    private static IResult InvalidCursor() => Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", $"{resource} ID must be a ULID.");
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The requested resource does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record TeamContract(
    string Id, string Key, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record TeamPage(IReadOnlyList<TeamContract> Items, string? NextCursor);
public sealed record SpecialtyContract(
    string Id, string Key, string Name, string? Description, string? TeamId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SpecialtyPage(IReadOnlyList<SpecialtyContract> Items, string? NextCursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TeamWriteRequest(string Name, string? Key = null, string? Description = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SpecialtyWriteRequest(string Name, string? Key = null, string? Description = null, string? TeamId = null);
