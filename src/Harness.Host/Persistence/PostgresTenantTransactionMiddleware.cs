using Harness.Host.Profiles;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Postgres;

namespace Harness.Host.Persistence;

/// <summary>
/// Places authenticated server-mode API work in a tenant scope. PostgreSQL stores
/// then select a tenant-specific pool whose startup options use the runtime role
/// and tenant GUC, including inside each store's native production transaction.
/// </summary>
public sealed class PostgresTenantTransactionMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next =
        next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(
        HttpContext context,
        ILocalProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal) ||
            !LocalProfileSession.TryGetProfileId(context.Request, out var profileId))
        {
            await _next(context);
            return;
        }

        // Profile lookup is the narrow bootstrap boundary: it resolves the tenant
        // before the runtime role is selected. All endpoint persistence below runs
        // with FORCE RLS and the resolved tenant in one transaction.
        var profile = await profiles.GetAsync(profileId, context.RequestAborted);
        if (profile is null)
        {
            await _next(context);
            return;
        }

        using var tenantScope = PostgresTenantDataSource.Enter(profile.TenantId);
        await _next(context);
    }
}
