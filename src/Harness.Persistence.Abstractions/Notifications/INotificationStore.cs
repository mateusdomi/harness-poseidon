namespace Harness.Persistence.Abstractions.Notifications;

public interface INotificationStore
{
    Task<IReadOnlyList<NotificationRecord>> ListAsync(string tenantId, string profileId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task<NotificationRecord?> GetAsync(string tenantId, string profileId, string id, CancellationToken cancellationToken = default);
    Task<NotificationRecord> CreateAsync(NotificationCreateCommand command, CancellationToken cancellationToken = default);
    Task<int> SetStatusAsync(NotificationStatusCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SettingsRecord>> ListSettingsAsync(string tenantId, string profileId, CancellationToken cancellationToken = default);
    Task<SettingsRecord?> GetSettingsAsync(string tenantId, string profileId, string id, CancellationToken cancellationToken = default);
    Task<SettingsRecord> UpdateSettingsAsync(SettingsUpdateCommand command, CancellationToken cancellationToken = default);
}

public sealed record NotificationRecord(string Id, string ProfileId, string Severity, string Category,
    string Title, string Body, string? GroupKey, int DedupeCount, string Status, string? Link,
    DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record SettingsRecord(string Id, string ProfileId, string Theme, string Language,
    bool NotificationsEnabled, IReadOnlyList<string> MutedCategories, string? WorkingDirectory,
    DateTimeOffset? UnsafeModeAcceptedAt, DateTimeOffset UpdatedAt);
public sealed record NotificationCreateCommand(string TenantId, string Id, string ProfileId,
    string Severity, string Category, string Title, string Body, string? GroupKey, string? Link,
    DateTimeOffset OccurredAt);
public sealed record NotificationStatusCommand(string TenantId, string ProfileId,
    IReadOnlyList<string> Ids, string Status, DateTimeOffset OccurredAt);
public sealed record SettingsUpdateCommand(string TenantId, string ProfileId, string Id,
    string PatchJson, DateTimeOffset OccurredAt);
public sealed class NotificationValidationException(string detail) : Exception(detail);
public sealed class NotificationNotFoundException(string resource) : Exception(resource) { public string Resource { get; } = resource; }
