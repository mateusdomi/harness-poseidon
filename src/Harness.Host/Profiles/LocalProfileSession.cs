using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Profiles;

internal static class LocalProfileSession
{
    public const string CookieName = "harness.profile";

    public static async Task<LocalProfileRecord?> ResolveAsync(
        HttpRequest request,
        ILocalProfileStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        if (!request.Cookies.TryGetValue(CookieName, out var profileId) ||
            !UlidValue.TryParse(profileId, out _))
        {
            return null;
        }

        return await store.GetAsync(profileId, cancellationToken);
    }
}
