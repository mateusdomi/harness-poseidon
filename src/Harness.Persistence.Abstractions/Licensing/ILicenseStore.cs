namespace Harness.Persistence.Abstractions.Licensing;

public interface ILicenseStore
{
    Task<LicenseRecord> GetOrCreateAsync(
        LicenseBootstrapCommand command, CancellationToken cancellationToken = default);
    Task<LicenseRecord?> GetAsync(
        string tenantId, string licenseId, CancellationToken cancellationToken = default);
    Task<LicenseRecord> ActivateAsync(
        LicenseActivationCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntitlementRecord>> ListEntitlementsAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<EntitlementRecord?> GetEntitlementAsync(
        string tenantId, string entitlementId, CancellationToken cancellationToken = default);
}

public sealed record LicenseRecord(
    string TenantId, string Id, string State, string Plan, string DeviceId, string DeviceName,
    DateTimeOffset? ExpiresAt, DateTimeOffset? GracePeriodEndsAt, bool OfflineMode,
    DateTimeOffset? LastValidatedAt);

public sealed record EntitlementRecord(
    string TenantId, string Id, string LicenseId, string Key, string Description,
    bool Included, int? Limit);

public sealed record LicenseBootstrapCommand(
    string TenantId, string LicenseId, string DeviceId, string DeviceName,
    DateTimeOffset OccurredAt);

public sealed record LicenseActivationCommand(
    string TenantId, string ProfileId, string Key, DateTimeOffset OccurredAt);

public sealed class LicenseNotFoundException : Exception;
