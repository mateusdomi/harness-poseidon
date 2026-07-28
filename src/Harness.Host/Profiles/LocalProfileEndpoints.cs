using Harness.Modules.Identity.Application;
using Harness.Modules.Identity.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Profiles;

public static class LocalProfileEndpoints
{
    public static IEndpointRouteBuilder MapLocalProfiles(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/v1/profiles").WithTags("profiles");
        group.MapGet("/current", GetCurrentAsync).Produces<ProfileResponse>().ProducesProblem(404);
        group.MapGet("/", ListAsync).Produces<ProfilePage>().ProducesProblem(400);
        group.MapPost("/", CreateAsync).Produces<ProfileResponse>(201).ProducesProblem(400).ProducesProblem(409);
        group.MapPatch("/{profileId}", PatchAsync).Produces<ProfileResponse>()
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> GetCurrentAsync(
        HttpRequest request,
        ILocalProfileStore store,
        CancellationToken cancellationToken)
    {
        if (!LocalProfileSession.TryGetProfileId(request, out var profileId))
        {
            return Problem(404, "profile_not_found", "No local profile session exists.");
        }

        var profile = await store.GetAsync(profileId, cancellationToken);
        return profile is null
            ? Problem(404, "profile_not_found", "The local profile does not exist.")
            : Results.Ok(ToResponse(profile));
    }

    private static async Task<IResult> ListAsync(
        string? cursor,
        int? limit,
        ILocalProfileStore store,
        CancellationToken cancellationToken)
    {
        var pageSize = limit ?? 50;
        if (pageSize is < 1 or > 200 ||
            (cursor is not null && !UlidValue.TryParse(cursor, out _)))
        {
            return Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
        }

        var profiles = await store.ListAsync(cancellationToken);
        var filtered = profiles
            .Where(item => cursor is null || string.CompareOrdinal(item.Id, cursor) > 0)
            .Take(pageSize + 1)
            .ToArray();
        var hasMore = filtered.Length > pageSize;
        var items = filtered.Take(pageSize).Select(ToResponse).ToArray();
        return Results.Ok(new ProfilePage(items, hasMore ? items[^1].Id : null));
    }

    private static async Task<IResult> CreateAsync(
        CreateProfileRequest request,
        HttpResponse response,
        ILocalProfileStore store,
        Workflows.WorkflowTemplateSeeder workflowTemplates,
        HarnessServerOptions serverOptions,
        IClock clock,
        CancellationToken cancellationToken)
    {
        try
        {
            var occurredAt = clock.UtcNow;
            var tenantId = UlidValue.New(occurredAt).ToString();
            var joinExistingTenant = false;
            if (serverOptions.Multiuser)
            {
                var existing = await store.ListAsync(cancellationToken);
                if (existing.Count > 0)
                {
                    tenantId = existing[0].TenantId;
                    joinExistingTenant = true;
                }
            }

            var profileId = UlidValue.New(occurredAt).ToString();
            var profile = LocalProfileApplicationService.Create(profileId, request, occurredAt);
            var result = await store.CreateAsync(
                new LocalProfileCreateCommand(
                    tenantId, "Personal", profile.Id, profile.DisplayName, profile.Email,
                    profile.AvatarUrl, profile.Locale, occurredAt, joinExistingTenant),
                cancellationToken);
            if (result.Status is LocalProfileMutationStatus.AlreadyExists &&
                serverOptions.Multiuser && !joinExistingTenant)
            {
                // Corrida de bootstrap multiusuário: outro perfil venceu a criação do
                // tenant entre a listagem e o INSERT; repete como adesão ao tenant dele.
                var winners = await store.ListAsync(cancellationToken);
                result = await store.CreateAsync(
                    new LocalProfileCreateCommand(
                        winners[0].TenantId, "Personal", profile.Id, profile.DisplayName,
                        profile.Email, profile.AvatarUrl, profile.Locale, occurredAt,
                        JoinExistingTenant: true),
                    cancellationToken);
            }

            if (result.Status is LocalProfileMutationStatus.AlreadyExists)
            {
                return Problem(409, "profile_already_exists", "A local profile already exists.");
            }

            if (result.Status is LocalProfileMutationStatus.NotFound)
            {
                return Problem(409, "tenant_not_found", "The shared tenant no longer exists.");
            }

            var created = result.Profile
                ?? throw new InvalidOperationException("Applied profile creation returned no profile.");
            await workflowTemplates.EnsureSeededAsync(created.TenantId, cancellationToken);
            SetSessionCookie(response, created.Id);
            return Results.Created($"/api/v1/profiles/{created.Id}", ToResponse(created));
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_profile", exception.Message);
        }
    }

    private static async Task<IResult> PatchAsync(
        string profileId,
        UpdateProfileRequest patch,
        HttpRequest request,
        ILocalProfileStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(profileId, out _))
        {
            return Problem(400, "invalid_profile_id", "Profile ID must be a ULID.");
        }

        if (!LocalProfileSession.TryGetProfileId(request, out var currentId))
        {
            return Problem(403, "profile_forbidden", "The local session cannot update this profile.");
        }

        if (!string.Equals(currentId, profileId, StringComparison.Ordinal))
        {
            // ABAC: o dono sempre pode editar o próprio perfil; RBAC: admin do
            // tenant pode editar qualquer perfil (modo servidor multiusuário).
            var session = await store.GetAsync(currentId, cancellationToken);
            if (session is null || session.Role != LocalProfileRole.Admin)
            {
                return Problem(403, "profile_forbidden", "The local session cannot update this profile.");
            }
        }

        var current = await store.GetAsync(profileId, cancellationToken);
        if (current is null)
        {
            return Problem(404, "profile_not_found", "The local profile does not exist.");
        }

        try
        {
            var occurredAt = clock.UtcNow;
            var updated = LocalProfileApplicationService.Patch(ToContract(current), patch, occurredAt);
            var result = await store.UpdateAsync(
                new LocalProfileUpdateCommand(
                    updated.Id, updated.DisplayName, updated.Email, updated.AvatarUrl,
                    updated.Locale, current.Version, occurredAt),
                cancellationToken);
            return result.Status switch
            {
                LocalProfileMutationStatus.Applied => Results.Ok(ToResponse(result.Profile!)),
                LocalProfileMutationStatus.NotFound => Problem(404, "profile_not_found", "The local profile does not exist."),
                LocalProfileMutationStatus.VersionConflict => Problem(409, "profile_version_conflict", "The profile changed concurrently."),
                _ => throw new InvalidOperationException($"Unexpected profile update status: {result.Status}."),
            };
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_profile", exception.Message);
        }
    }

    private static ProfileContract ToContract(LocalProfileRecord profile) =>
        new(profile.Id, profile.DisplayName, profile.Email, profile.AvatarUrl, profile.Locale,
            profile.CreatedAt, profile.LastActiveAt, profile.Version);

    private static ProfileResponse ToResponse(LocalProfileRecord profile) =>
        new(profile.Id, profile.DisplayName, profile.Email, profile.AvatarUrl, profile.Locale,
            LocalProfileRoleCodec.ToStorage(profile.Role), profile.CreatedAt, profile.LastActiveAt);

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

    private static void SetSessionCookie(HttpResponse response, string profileId) =>
        response.Cookies.Append(
            LocalProfileSession.CookieName,
            profileId,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                // Sob TLS (modo servidor) o cookie de sessão exige canal seguro; em
                // loopback HTTP do modo pessoal ele precisa continuar sendo enviado.
                Secure = response.HttpContext.Request.IsHttps,
                Path = "/",
            });
}

public sealed record ProfileResponse(
    string Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale,
    string Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt);

public sealed record ProfilePage(IReadOnlyList<ProfileResponse> Items, string? NextCursor);
