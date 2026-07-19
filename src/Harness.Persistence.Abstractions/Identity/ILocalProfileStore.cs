namespace Harness.Persistence.Abstractions.Identity;

public interface ILocalProfileStore
{
    Task<LocalProfileRecord?> GetAsync(string profileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalProfileRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task<LocalProfileMutationResult> CreateAsync(
        LocalProfileCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<LocalProfileMutationResult> UpdateAsync(
        LocalProfileUpdateCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record LocalProfileRecord(
    string TenantId,
    string Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    long Version,
    LocalProfileRole Role);

public sealed record LocalProfileCreateCommand(
    string TenantId,
    string TenantName,
    string ProfileId,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale,
    DateTimeOffset OccurredAt,
    bool JoinExistingTenant = false);

public sealed record LocalProfileUpdateCommand(
    string ProfileId,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale,
    long ExpectedVersion,
    DateTimeOffset OccurredAt);

public enum LocalProfileMutationStatus
{
    Applied,
    AlreadyExists,
    NotFound,
    VersionConflict,
}

public sealed record LocalProfileMutationResult(
    LocalProfileMutationStatus Status,
    LocalProfileRecord? Profile = null);

public enum LocalProfileRole
{
    Admin,
    Member,
}

public static class LocalProfileRoleCodec
{
    public static string ToStorage(LocalProfileRole role) => role switch
    {
        LocalProfileRole.Admin => "admin",
        LocalProfileRole.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown local profile role."),
    };

    public static LocalProfileRole Parse(string value) => value switch
    {
        "admin" => LocalProfileRole.Admin,
        "member" => LocalProfileRole.Member,
        _ => throw new ArgumentException("Unknown local profile role value.", nameof(value)),
    };
}
