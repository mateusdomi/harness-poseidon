using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Profiles;

internal static class LocalProfileSession
{
    public const string CookieName = "harness.profile";

    /// <summary>
    /// Perfil resolvido por autenticação OIDC nesta requisição; tem precedência
    /// sobre o cookie de sessão local.
    /// </summary>
    public const string ItemKey = "harness.session.profileId";

    public static bool TryGetProfileId(HttpRequest request, out string profileId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HttpContext.Items.TryGetValue(ItemKey, out var item) &&
            item is string resolved && UlidValue.TryParse(resolved, out _))
        {
            profileId = resolved;
            return true;
        }

        if (request.Cookies.TryGetValue(CookieName, out var cookie) &&
            cookie is not null && UlidValue.TryParse(cookie, out _))
        {
            profileId = cookie;
            return true;
        }

        profileId = string.Empty;
        return false;
    }

    public static async Task<LocalProfileRecord?> ResolveAsync(
        HttpRequest request,
        ILocalProfileStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        return TryGetProfileId(request, out var profileId)
            ? await store.GetAsync(profileId, cancellationToken)
            : null;
    }
}
