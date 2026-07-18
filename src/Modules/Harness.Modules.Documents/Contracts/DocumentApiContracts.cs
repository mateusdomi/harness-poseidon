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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDocumentRequest(
    string ProjectId, string Title, string Kind, string Body,
    IReadOnlyList<string>? Classifications = null, string? PhaseName = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDocumentVersionRequest(string DocumentId, string Body);
