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
public sealed record StartChatTurnRequest(
    string Content, string? AccountId = null, string? ModelId = null, string? Effort = null,
    IReadOnlyList<string>? FallbackModelIds = null, string? SelectionReason = null);

public sealed record ChatTurnHandle(string TurnId, string ConversationId);
