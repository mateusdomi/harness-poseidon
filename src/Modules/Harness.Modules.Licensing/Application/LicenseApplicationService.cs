using System.Text.RegularExpressions;
using Harness.Modules.Licensing.Contracts;

namespace Harness.Modules.Licensing.Application;

public static partial class LicenseApplicationService
{
    public static string ValidateActivation(ActivateLicenseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        var key = request.Key.Trim().ToUpperInvariant();
        return ActivationKey().IsMatch(key)
            ? key
            : throw new ArgumentException(
                "The key must use the XXXX-XXXX-XXXX-XXXX format.", nameof(request));
    }

    public static string ResolveState(
        string storedState, bool offlineMode, DateTimeOffset? expiresAt,
        DateTimeOffset? gracePeriodEndsAt, DateTimeOffset now)
    {
        if (storedState == "unlicensed") return "unlicensed";
        if (expiresAt is null || expiresAt > now) return offlineMode ? "offline" : "active";
        if (gracePeriodEndsAt is not null && gracePeriodEndsAt > now) return "gracePeriod";
        return "expired";
    }

    [GeneratedRegex("^[A-Z0-9]{4}(?:-[A-Z0-9]{4}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ActivationKey();
}
