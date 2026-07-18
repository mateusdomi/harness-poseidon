using Harness.Host.Profiles;
using Harness.Modules.Licensing.Application;
using Harness.Modules.Licensing.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Licensing;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Licensing;

public static class LicenseEndpoints
{
    public static IEndpointRouteBuilder MapLicensing(this IEndpointRouteBuilder endpoints)
    {
        var licenses = endpoints.MapGroup("/api/v1/licenses").WithTags("licenses");
        licenses.MapGet("/", ListLicensesAsync).Produces<LicensePage>().ProducesProblem(400).ProducesProblem(401);
        licenses.MapGet("/{id}", GetLicenseAsync).Produces<LicenseContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        licenses.MapPost("/activation", ActivateAsync).Produces<LicenseContract>().ProducesProblem(400).ProducesProblem(401);
        var entitlements = endpoints.MapGroup("/api/v1/entitlements").WithTags("entitlements");
        entitlements.MapGet("/", ListEntitlementsAsync).Produces<EntitlementPage>().ProducesProblem(400).ProducesProblem(401);
        entitlements.MapGet("/{id}", GetEntitlementAsync).Produces<EntitlementContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListLicensesAsync(
        string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        ILicenseStore store, IClock clock, CancellationToken token)
    {
        var invalid = Page(cursor, limit); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return Unauthorized();
        var license = await EnsureAsync(profile, store, clock.UtcNow, token);
        var items = cursor is null || string.CompareOrdinal(license.Id, cursor) > 0
            ? new[] { ToContract(license, clock.UtcNow) }
            : [];
        return Results.Ok(new LicensePage(items, null));
    }

    private static async Task<IResult> GetLicenseAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, ILicenseStore store,
        IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("license");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return Unauthorized();
        await EnsureAsync(profile, store, clock.UtcNow, token);
        var value = await store.GetAsync(profile.TenantId, id, token);
        return value is null ? NotFound("license") : Results.Ok(ToContract(value, clock.UtcNow));
    }

    private static async Task<IResult> ActivateAsync(
        ActivateLicenseRequest input, HttpRequest request, ILocalProfileStore profiles,
        ILicenseStore store, IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return Unauthorized();
        try
        {
            var key = LicenseApplicationService.ValidateActivation(input); var now = clock.UtcNow;
            await EnsureAsync(profile, store, now, token);
            var value = await store.ActivateAsync(new(profile.TenantId, profile.Id, key, now), token);
            return Results.Ok(ToContract(value, now));
        }
        catch (ArgumentException e) { return Invalid("license_key", e.Message); }
    }

    private static async Task<IResult> ListEntitlementsAsync(
        string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles,
        ILicenseStore store, CancellationToken token)
    {
        var invalid = Page(cursor, limit); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return Unauthorized();
        var size = limit ?? 50; var rows = await store.ListEntitlementsAsync(
            profile.TenantId, cursor, size + 1, token); var more = rows.Count > size;
        var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new EntitlementPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetEntitlementAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, ILicenseStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("entitlement");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return Unauthorized();
        var value = await store.GetEntitlementAsync(profile.TenantId, id, token);
        return value is null ? NotFound("entitlement") : Results.Ok(ToContract(value));
    }

    private static Task<LicenseRecord> EnsureAsync(
        LocalProfileRecord profile, ILicenseStore store, DateTimeOffset now, CancellationToken token)
    {
        var id = UlidValue.New(now).ToString();
        var deviceName = string.IsNullOrWhiteSpace(Environment.MachineName)
            ? "Local device"
            : Environment.MachineName[..Math.Min(Environment.MachineName.Length, 200)];
        return store.GetOrCreateAsync(new(profile.TenantId, id, $"device-{id[..12]}", deviceName, now), token);
    }

    private static LicenseContract ToContract(LicenseRecord value, DateTimeOffset now) => new(
        value.Id, LicenseApplicationService.ResolveState(value.State, value.OfflineMode,
            value.ExpiresAt, value.GracePeriodEndsAt, now), value.Plan, value.DeviceId,
        value.DeviceName, value.ExpiresAt, value.GracePeriodEndsAt, value.OfflineMode,
        value.LastValidatedAt);
    private static EntitlementContract ToContract(EntitlementRecord value) => new(
        value.Id, value.Key, value.Description, value.Included, value.Limit);
    private static IResult? Page(string? cursor, int? limit) =>
        (cursor is not null && !Valid(cursor)) || limit is < 1 or > 200
            ? Invalid("pagination", "Cursor or limit is invalid.") : null;
    private static bool Valid(string value) => UlidValue.TryParse(value, out _);
    private static IResult Unauthorized() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult InvalidId(string resource) => Invalid($"{resource}_id", "ID must be a ULID.");
    private static IResult Invalid(string title, string detail) => Problem(400, $"invalid_{title}", detail);
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist.");
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record LicensePage(IReadOnlyList<LicenseContract> Items, string? NextCursor);
public sealed record EntitlementPage(IReadOnlyList<EntitlementContract> Items, string? NextCursor);
