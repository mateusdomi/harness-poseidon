using System.Text.Json;
using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Notifications;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Notifications;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder endpoints)
    {
        var notifications = endpoints.MapGroup("/api/v1/notifications").WithTags("notifications");
        notifications.MapGet("/", ListAsync).Produces<NotificationPage>().ProducesProblem(400).ProducesProblem(401);
        notifications.MapGet("/{id}", GetAsync).Produces<NotificationContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        notifications.MapPost("/", CreateAsync).Produces<NotificationContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        notifications.MapPost("/read", ReadAsync).Produces<NotificationStatusResult>().ProducesProblem(400).ProducesProblem(401);
        notifications.MapPost("/mute", MuteAsync).Produces<NotificationStatusResult>().ProducesProblem(400).ProducesProblem(401);
        var settings = endpoints.MapGroup("/api/v1/settings").WithTags("settings");
        settings.MapGet("/", ListSettingsAsync).Produces<SettingsPage>().ProducesProblem(401);
        settings.MapGet("/{id}", GetSettingsAsync).Produces<SettingsContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        settings.MapPatch("/{id}", PatchSettingsAsync).Accepts<SettingsPatchRequest>("application/json").Produces<SettingsContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(string? profileId, string? cursor, int? limit, string? status, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, CancellationToken token)
    {
        var session = await SessionAsync(request, profiles, token); if (session is null) return Unauthorized();
        if (!Owned(profileId, session.Id) || cursor is not null && !UlidValue.TryParse(cursor, out _) || limit is < 1 or > 200 || status is not null && status is not ("unread" or "read" or "muted")) return Invalid("invalid_notification_query", "Notification query is invalid.");
        var size = limit ?? 50; var rows = await store.ListAsync(session.TenantId, session.Id, cursor, size + 1, status, token); var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new NotificationPage(items, rows.Count > size ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAsync(string id, string? profileId, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return Invalid("invalid_notification_id", "Notification ID must be a ULID.");
        var session = await SessionAsync(request, profiles, token); if (session is null) return Unauthorized(); if (!Owned(profileId, session.Id)) return Forbidden();
        var value = await store.GetAsync(session.TenantId, session.Id, id, token); return value is null ? Missing("notification") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> CreateAsync(NotificationCreateRequest input, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, IClock clock, CancellationToken token)
    {
        var session = await SessionAsync(request, profiles, token); if (session is null) return Unauthorized(); if (!Owned(input.ProfileId, session.Id)) return Forbidden();
        var now = clock.UtcNow; var id = UlidValue.New(now).ToString();
        try { var value = await store.CreateAsync(new(session.TenantId, id, session.Id, input.Severity, input.Category, input.Title, input.Body, input.GroupKey, input.Link, now), token); return Results.Created($"/api/v1/notifications/{value.Id}", ToContract(value)); }
        catch (NotificationValidationException e) { return Invalid("invalid_notification", e.Message); }
        catch (NotificationNotFoundException e) { return Missing(e.Resource); }
    }

    private static Task<IResult> ReadAsync(NotificationStatusRequest input, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, IClock clock, CancellationToken token) => SetStatusAsync(input, "read", request, profiles, store, clock, token);
    private static Task<IResult> MuteAsync(NotificationStatusRequest input, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, IClock clock, CancellationToken token) => SetStatusAsync(input, "muted", request, profiles, store, clock, token);
    private static async Task<IResult> SetStatusAsync(NotificationStatusRequest input, string status, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, IClock clock, CancellationToken token)
    {
        var session = await SessionAsync(request, profiles, token); if (session is null) return Unauthorized();
        try { var changed = await store.SetStatusAsync(new(session.TenantId, session.Id, input.Ids, status, clock.UtcNow), token); return Results.Ok(new NotificationStatusResult(changed)); }
        catch (NotificationValidationException e) { return Invalid("invalid_notification_status", e.Message); }
    }

    private static async Task<IResult> ListSettingsAsync(string? profileId, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, CancellationToken token)
    {
        var session = await SessionAsync(request, profiles, token); if (session is null) return Unauthorized(); if (!Owned(profileId, session.Id)) return Forbidden();
        return Results.Ok(new SettingsPage((await store.ListSettingsAsync(session.TenantId, session.Id, token)).Select(ToContract).ToArray(), null));
    }

    private static async Task<IResult> GetSettingsAsync(string id, string? profileId, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return Invalid("invalid_settings_id", "Settings ID must be a ULID."); var session = await SessionAsync(request, profiles, token); if (session is null) return Unauthorized(); if (!Owned(profileId, session.Id)) return Forbidden();
        var value = await store.GetSettingsAsync(session.TenantId, session.Id, id, token); return value is null ? Missing("settings") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> PatchSettingsAsync(string id, HttpRequest request, ILocalProfileStore profiles, INotificationStore store, IClock clock, CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _)) return Invalid("invalid_settings_id", "Settings ID must be a ULID."); var session = await SessionAsync(request, profiles, token); if (session is null) return Unauthorized();
        try { var patch = await request.ReadFromJsonAsync<JsonElement>(cancellationToken: token); var value = await store.UpdateSettingsAsync(new(session.TenantId, session.Id, id, patch.GetRawText(), clock.UtcNow), token); return Results.Ok(ToContract(value)); }
        catch (NotificationValidationException e) { return Invalid("invalid_settings", e.Message); }
        catch (NotificationNotFoundException e) { return Missing(e.Resource); }
        catch (JsonException e) { return Invalid("invalid_settings", e.Message); }
    }

    private static Task<LocalProfileRecord?> SessionAsync(HttpRequest request, ILocalProfileStore profiles, CancellationToken token) => LocalProfileSession.ResolveAsync(request, profiles, token);
    private static bool Owned(string? requested, string current) => requested is null || string.Equals(requested, current, StringComparison.Ordinal);
    private static NotificationContract ToContract(NotificationRecord x) => new(x.Id, x.ProfileId, x.Severity, x.Category, x.Title, x.Body, x.GroupKey, x.DedupeCount, x.Status, x.Link, x.CreatedAt, x.ReadAt);
    private static SettingsContract ToContract(SettingsRecord x) => new(x.Id, x.ProfileId, x.Theme, x.Language, x.NotificationsEnabled, x.MutedCategories, x.WorkingDirectory, x.UpdatedAt);
    private static IResult Unauthorized() => Results.Problem(statusCode: 401, title: "local_session_required", detail: "A local profile session is required.");
    private static IResult Forbidden() => Results.Problem(statusCode: 403, title: "profile_scope_forbidden", detail: "The resource belongs to another profile.");
    private static IResult Missing(string resource) => Results.Problem(statusCode: 404, title: $"{resource}_not_found", detail: $"The {resource} resource does not exist.");
    private static IResult Invalid(string title, string detail) => Results.Problem(statusCode: 400, title: title, detail: detail);
}

public sealed record NotificationCreateRequest(string ProfileId, string Severity, string Category, string Title, string Body, string? GroupKey, string? Link);
public sealed record NotificationStatusRequest(IReadOnlyList<string> Ids);
public sealed record NotificationStatusResult(int Updated);
public sealed record NotificationContract(string Id, string ProfileId, string Severity, string Category, string Title, string Body, string? GroupKey, int DedupeCount, string Status, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record NotificationPage(IReadOnlyList<NotificationContract> Items, string? NextCursor);
public sealed record SettingsPatchRequest(string? Theme, string? Language, bool? NotificationsEnabled, IReadOnlyList<string>? MutedCategories, string? WorkingDirectory);
public sealed record SettingsContract(string Id, string ProfileId, string Theme, string Language, bool NotificationsEnabled, IReadOnlyList<string> MutedCategories, string? WorkingDirectory, DateTimeOffset UpdatedAt);
public sealed record SettingsPage(IReadOnlyList<SettingsContract> Items, string? NextCursor);
