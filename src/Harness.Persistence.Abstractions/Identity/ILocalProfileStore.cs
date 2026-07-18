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
    long Version);

public sealed record LocalProfileCreateCommand(
    string TenantId,
    string TenantName,
    string ProfileId,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Locale,
    DateTimeOffset OccurredAt);

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
