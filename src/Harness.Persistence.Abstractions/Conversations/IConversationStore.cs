namespace Harness.Persistence.Abstractions.Conversations;

public interface IConversationStore
{
    Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        string? projectId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ConversationMutationResult> CreateConversationAsync(
        ConversationCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<ConversationMutationResult> DeleteConversationAsync(
        string tenantId,
        string conversationId,
        long expectedVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<MessageRecord?> GetMessageAsync(
        string tenantId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        string tenantId,
        string? conversationId,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MessageMutationResult> CreateMessageAsync(
        MessageCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<ChatTurnMutationResult> StartTurnAsync(
        ChatTurnCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record ConversationRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string Title,
    string State,
    string CreatedByProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    long Version);

public sealed record MessageRecord(
    string TenantId,
    string ProjectId,
    string Id,
    string ConversationId,
    string AuthorRole,
    string? AuthorProfileId,
    string? AuthorAgentId,
    string Content,
    int? TokenCount,
    DateTimeOffset CreatedAt);

public sealed record ConversationCreateCommand(
    ConversationRecord Conversation,
    DateTimeOffset OccurredAt);

public sealed record MessageCreateCommand(
    string TenantId,
    MessageRecord Message,
    DateTimeOffset OccurredAt);

public sealed record ChatTurnCommand(
    string TenantId,
    string ConversationId,
    string TurnId,
    MessageRecord UserMessage,
    MessageRecord ChiefMessage,
    IReadOnlyList<string> Chunks,
    DateTimeOffset OccurredAt);

public enum ConversationMutationStatus
{
    Applied,
    ProjectNotFound,
    NotFound,
    VersionConflict,
    Inactive,
    AlreadyExists,
}

public sealed record ConversationMutationResult(
    ConversationMutationStatus Status,
    ConversationRecord? Conversation = null);

public enum MessageMutationStatus
{
    Applied,
    ConversationNotFound,
    ConversationInactive,
    AlreadyExists,
}

public sealed record MessageMutationResult(
    MessageMutationStatus Status,
    MessageRecord? Message = null);

public sealed record ChatTurnMutationResult(
    MessageMutationStatus Status,
    string TurnId,
    string ConversationId);
