using System.Text.Json.Serialization;

namespace Harness.Modules.Conversations.Contracts;

public sealed record ConversationContract(
    string Id,
    string ProjectId,
    string Title,
    string State,
    string CreatedByProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    long Version);

public sealed record MessageContract(
    string Id,
    string ConversationId,
    string AuthorRole,
    string? AuthorProfileId,
    string? AuthorAgentId,
    string Content,
    int? TokenCount,
    DateTimeOffset CreatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateConversationRequest(string ProjectId, string Title);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateMessageRequest(string ConversationId, string Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RememberActiveConversationRequest(string ConversationId);

public sealed record ActiveConversationResponse(string ConversationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartChatTurnRequest(
    string Content, string? AccountId = null, string? ModelId = null, string? Effort = null,
    IReadOnlyList<string>? FallbackModelIds = null, string? SelectionReason = null);

/// <summary>Bloqueador tipado do turno: código estável e IDs relacionados, nunca texto livre.</summary>
public sealed record ChatTurnBlocker(string Code, IReadOnlyList<string> RelatedIds);

/// <summary>Próxima ação recomendada para desbloquear o turno: código, rota e recurso.</summary>
public sealed record ChatTurnNextAction(string Code, string Route, string? ResourceId);

/// <summary>Prontidão observada no momento do turno (ADR-017).</summary>
public sealed record ChatTurnReadiness(string OverallState, string ExecutionState);

/// <summary>Links de navegação/diagnóstico do turno.</summary>
public sealed record ChatTurnLinks(string Readiness, string Conversation);

/// <summary>
/// Estado tipado do turno (ADR-019/C2). `State` segue a nomenclatura do domínio:
/// `pending` (registrado e enfileirado), `processing`, `completed`, `failed` e `blocked`
/// (mensagem persistida, execução recusada por prontidão — nenhuma resposta é fabricada).
/// Um turno bloqueado não é erro de requisição: `400` fica reservado a request inválido.
/// </summary>
public sealed record ChatTurnHandle(
    string TurnId,
    string ConversationId,
    string State,
    string CorrelationId,
    ChatTurnReadiness Readiness,
    IReadOnlyList<ChatTurnBlocker> Blockers,
    IReadOnlyList<ChatTurnNextAction> NextActions,
    ChatTurnLinks Links);
