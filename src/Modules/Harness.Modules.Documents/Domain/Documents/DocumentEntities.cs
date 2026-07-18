using Harness.Modules.Documents.Contracts;
using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Documents.Domain.Documents;

public sealed record DocumentVersion(
    EntityId<DocumentVersionTag> Id,
    int Version,
    string CatalogPath,
    string ContentHash,
    EntityId<DocumentVersionTag>? SupersedesId,
    DocumentAuthorKind AuthorKind,
    string? AuthorId,
    DateTimeOffset CreatedAt);

public sealed class DocumentApprovalRequest
{
    internal DocumentApprovalRequest(
        EntityId<DocumentApprovalRequestTag> id,
        EntityId<DocumentTag> documentId,
        EntityId<DocumentVersionTag> documentVersionId,
        string title,
        string description,
        DocumentApprovalPriority priority,
        DateTimeOffset? dueAt,
        string requestedByAgentId,
        DateTimeOffset requestedAt)
    {
        Id = id;
        DocumentId = documentId;
        DocumentVersionId = documentVersionId;
        Title = title;
        Description = description;
        Priority = priority;
        DueAt = dueAt;
        RequestedByAgentId = requestedByAgentId;
        RequestedAt = requestedAt;
        State = DocumentApprovalState.Pending;
    }

    public EntityId<DocumentApprovalRequestTag> Id { get; }

    public EntityId<DocumentTag> DocumentId { get; }

    public EntityId<DocumentVersionTag> DocumentVersionId { get; }

    public string Title { get; }

    public string Description { get; }

    public DocumentApprovalPriority Priority { get; }

    public DateTimeOffset? DueAt { get; }

    public string RequestedByAgentId { get; }

    public DateTimeOffset RequestedAt { get; }

    public DocumentApprovalState State { get; internal set; }

    public string? ResolvedByProfileId { get; internal set; }

    public DateTimeOffset? ResolvedAt { get; internal set; }

    public string? ResolutionNote { get; internal set; }
}
