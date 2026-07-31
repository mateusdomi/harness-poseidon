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

    Task<ConversationMutationResult> RenameConversationAsync(
        string tenantId,
        string conversationId,
        long expectedVersion,
        string title,
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

    /// <summary>
    /// Fase 0A2 (BR-006): as ÚLTIMAS <paramref name="limit"/> mensagens da conversa, devolvidas em
    /// ordem cronológica. <see cref="ListMessagesAsync"/> ordena por id crescente, então em um
    /// projeto longo ele entregava as mensagens MAIS ANTIGAS e descartava tudo o que havia sido
    /// decidido recentemente — a Bruna respondia com o contexto do primeiro dia.
    ///
    /// A seleção é decrescente no armazenamento (o banco escolhe as últimas sem varrer a conversa
    /// inteira) e invertida antes de devolver, porque a montagem do contexto depende da ordem
    /// cronológica. A mensagem FUNDADORA vem separada, por <see cref="GetFirstMessageAsync"/>: ela
    /// é o mandato do projeto e não pode depender de caber na janela recente.
    /// </summary>
    Task<IReadOnlyList<MessageRecord>> ListRecentMessagesAsync(
        string tenantId,
        string conversationId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>A primeira mensagem da conversa — o mandato fundador — ou nulo se não houver.</summary>
    Task<MessageRecord?> GetFirstMessageAsync(
        string tenantId,
        string conversationId,
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
