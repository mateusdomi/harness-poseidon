namespace Harness.Persistence.Abstractions.Licensing;

public interface ISignedLicenseStore
{
    Task<SignedLicenseRecord> SaveAsync(
        SignedLicenseSaveCommand command,
        CancellationToken cancellationToken = default);

    Task<SignedLicenseRecord?> GetCurrentAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    Task<int> AddRevocationsAsync(
        string tenantId,
        IReadOnlyList<string> licenseIds,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);

    Task<bool> IsRevokedAsync(
        string tenantId,
        string licenseId,
        CancellationToken cancellationToken = default);
}

public sealed record SignedLicenseRecord(
    string TenantId,
    string LicenseId,
    string Licensee,
    string? DeviceFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int GraceDays,
    IReadOnlyList<string> Entitlements,
    string DocumentJson,
    string Signature,
    DateTimeOffset ActivatedAt);

public sealed record SignedLicenseSaveCommand(
    string TenantId,
    string LicenseId,
    string Licensee,
    string? DeviceFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int GraceDays,
    IReadOnlyList<string> Entitlements,
    string DocumentJson,
    string Signature,
    DateTimeOffset ActivatedAt);
