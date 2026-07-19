using System.Text.Json.Serialization;

namespace Harness.Modules.Licensing.Contracts;

public sealed record LicenseContract(
    string Id, string State, string Plan, string DeviceId, string DeviceName,
    DateTimeOffset? ExpiresAt, DateTimeOffset? GracePeriodEndsAt, bool OfflineMode,
    DateTimeOffset? LastValidatedAt);

public sealed record EntitlementContract(
    string Id, string Key, string Description, bool Included, int? Limit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActivateLicenseRequest(string Key);
