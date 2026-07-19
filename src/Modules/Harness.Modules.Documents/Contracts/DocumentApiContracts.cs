using System.Text.Json.Serialization;

namespace Harness.Modules.Documents.Contracts;

public sealed record DocumentWaiverContract(
    string Reason, string ApprovedByProfileId, DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt);

public sealed record DocumentContract(
    string Id, string ProjectId, string Title, string Kind, string State, int CurrentVersion,
    IReadOnlyList<string> Classifications, string? PhaseName, bool Inconsistent,
    DocumentWaiverContract? Waiver, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record DocumentVersionContract(
    string Id, string DocumentId, int Version, string Body, string AuthorKind, string? AuthorId,
    DateTimeOffset CreatedAt);

public sealed record ApprovalContract(
    string Id, string ProjectId, string? GateId, string? TaskId, string? DocumentId,
    string Title, string Description, string Priority, DateTimeOffset? DueAt, string State,
    string RequestedByAgentId, DateTimeOffset RequestedAt, string? ResolvedByProfileId,
    DateTimeOffset? ResolvedAt, string? ResolutionNote);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDocumentRequest(
    string ProjectId, string Title, string Kind, string Body,
    IReadOnlyList<string>? Classifications = null, string? PhaseName = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDocumentVersionRequest(string DocumentId, string Body);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveDocumentVersionRequest(string Body);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClassifyDocumentRequest(
    IReadOnlyList<string>? Classifications = null, string? PhaseName = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionDocumentRequest(string ToState, string? Note = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateApprovalRequest(
    string ProjectId, string Title, string Description, string RequestedByAgentId,
    string? GateId = null, string? TaskId = null, string? DocumentId = null,
    string? Priority = null, DateTimeOffset? DueAt = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResolveApprovalRequest(string Decision, string? Note = null);
