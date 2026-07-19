namespace Harness.Persistence.Abstractions.Conversations;

public interface IChannelLinkStore
{
    Task<ChannelLinkRecord> GetOrCreateAsync(
        ChannelLinkCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<ChannelLinkRecord?> GetAsync(
        string tenantId,
        string linkId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelLinkRecord>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}

public sealed record ChannelLinkRecord(
    string TenantId,
    string Id,
    string Kind,
    string ExternalIdentity,
    string ProfileId,
    string ProjectId,
    string ConversationId,
    DateTimeOffset LinkedAt);

public sealed record ChannelLinkCreateCommand(
    string TenantId,
    string Id,
    string Kind,
    string ExternalIdentity,
    string ProfileId,
    string ProjectId,
    string ConversationId,
    DateTimeOffset OccurredAt);
