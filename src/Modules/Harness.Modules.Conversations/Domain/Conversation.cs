using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Conversations.Domain;

public sealed record Conversation(
    string Id,
    string ProjectId,
    string Title,
    string State,
    string CreatedByProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    long Version)
{
    public static Conversation Create(
        string id,
        string projectId,
        string title,
        string createdByProfileId,
        DateTimeOffset occurredAt) =>
        new(
            RequiredId(id, nameof(id)),
            RequiredId(projectId, nameof(projectId)),
            Required(title, 200, nameof(title)),
            "active",
            RequiredId(createdByProfileId, nameof(createdByProfileId)),
            RequireUtc(occurredAt),
            null,
            1);

    public Conversation Archive(DateTimeOffset occurredAt)
    {
        _ = RequireUtc(occurredAt);
        if (!string.Equals(State, "active", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only an active conversation can be archived.");
        }

        return this with { State = "archived", Version = checked(Version + 1) };
    }

    public void EnsureTurnCanStart()
    {
        if (!string.Equals(State, "active", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Chat turns require an active conversation.");
        }
    }

    private static string RequiredId(string value, string name) =>
        UlidValue.TryParse(value, out var id)
            ? id.ToString()
            : throw new ArgumentException("Value must be a canonical ULID.", name);

    private static string Required(string value, int maximumLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentException($"Value exceeds {maximumLength} characters.", name);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed record ConversationMessage(
    string Id,
    string ConversationId,
    string AuthorRole,
    string? AuthorProfileId,
    string? AuthorAgentId,
    string Content,
    int? TokenCount,
    DateTimeOffset CreatedAt)
{
    private static readonly HashSet<string> Roles =
        new HashSet<string>(["user", "chief", "agent", "system"], StringComparer.Ordinal);

    public static ConversationMessage Create(
        string id,
        string conversationId,
        string authorRole,
        string? authorProfileId,
        string? authorAgentId,
        string content,
        int? tokenCount,
        DateTimeOffset occurredAt)
    {
        if (!Roles.Contains(authorRole))
        {
            throw new ArgumentException("Message author role is not supported.", nameof(authorRole));
        }

        if ((authorRole == "user") != (authorProfileId is not null) ||
            (authorRole is "chief" or "agent") != (authorAgentId is not null))
        {
            throw new ArgumentException("Message author identity does not match its role.", nameof(authorRole));
        }

        if (tokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenCount));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var normalized = content.Trim();
        if (normalized.Length > 100_000)
        {
            throw new ArgumentException("Message content exceeds 100000 characters.", nameof(content));
        }

        return new ConversationMessage(
            RequiredId(id, nameof(id)),
            RequiredId(conversationId, nameof(conversationId)),
            authorRole,
            authorProfileId is null ? null : RequiredId(authorProfileId, nameof(authorProfileId)),
            authorAgentId is null ? null : RequiredId(authorAgentId, nameof(authorAgentId)),
            normalized,
            tokenCount,
            RequireUtc(occurredAt));
    }

    private static string RequiredId(string value, string name) =>
        UlidValue.TryParse(value, out var id)
            ? id.ToString()
            : throw new ArgumentException("Value must be a canonical ULID.", name);

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
}
