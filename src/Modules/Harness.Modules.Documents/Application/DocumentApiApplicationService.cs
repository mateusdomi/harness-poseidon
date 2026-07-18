using System.Security.Cryptography;
using System.Text;
using Harness.Modules.Documents.Contracts;

namespace Harness.Modules.Documents.Application;

public static class DocumentApiApplicationService
{
    public static PreparedDocumentContent Prepare(
        string tenantId, string documentId, string versionId, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentNullException.ThrowIfNull(body);
        if (Encoding.UTF8.GetByteCount(body) > 10 * 1024 * 1024)
            throw new ArgumentException("Document body exceeds 10 MiB.", nameof(body));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return new($"documents/{tenantId}/{documentId}/{versionId}.md", hash, body);
    }

    public static (string Title, string Kind, IReadOnlyList<string> Classifications, string? PhaseName)
        Normalize(CreateDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Text(request.Title, 500);
        var kind = Text(request.Kind, 100);
        var classifications = (request.Classifications ?? [])
            .Select(value => Text(value, 100)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (classifications.Length > 50)
            throw new ArgumentException("At most 50 classifications are allowed.", nameof(request));
        var phase = request.PhaseName is null ? null : Text(request.PhaseName, 200);
        return (title, kind, classifications, phase);
    }

    public static string ToApiState(string state) => state switch
    {
        "in_elaboration" => "inElaboration",
        "in_review" => "inReview",
        "awaiting_approval" => "awaitingApproval",
        "not_applicable" => "notApplicable",
        _ => state,
    };

    public static string ToStoreState(string state) => state switch
    {
        "inElaboration" => "in_elaboration",
        "inReview" => "in_review",
        "awaitingApproval" => "awaiting_approval",
        "notApplicable" => "not_applicable",
        "planned" or "approved" or "outdated" or "superseded" => state,
        _ => throw new ArgumentException("Document state is invalid.", nameof(state)),
    };

    public static (IReadOnlyList<string> Classifications, string? PhaseName) Classify(
        ClassifyDocumentRequest request, IReadOnlyList<string> currentClassifications,
        string? currentPhaseName)
    {
        ArgumentNullException.ThrowIfNull(request);
        var classifications = (request.Classifications ?? currentClassifications)
            .Select(value => Text(value, 100)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (classifications.Length > 50)
            throw new ArgumentException("At most 50 classifications are allowed.", nameof(request));
        var phase = request.PhaseName is null ? currentPhaseName : Text(request.PhaseName, 200);
        return (classifications, phase);
    }

    public static (string Title, string Description, string Priority) Approval(
        CreateApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (new[] { request.DocumentId, request.GateId, request.TaskId }.Count(value => value is not null) > 1)
            throw new ArgumentException("Approval can reference at most one target.", nameof(request));
        var priority = request.Priority ?? "medium";
        if (priority is not ("low" or "medium" or "high" or "critical"))
            throw new ArgumentException("Approval priority is invalid.", nameof(request));
        return (Text(request.Title, 500), Text(request.Description, 10_000), priority);
    }

    public static (string Decision, string? Note) Resolution(ResolveApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Decision is not ("approved" or "rejected"))
            throw new ArgumentException("Approval decision is invalid.", nameof(request));
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : Text(request.Note, 10_000);
        if (request.Decision == "rejected" && note is null)
            throw new ArgumentException("A rejected approval requires a note.", nameof(request));
        return (request.Decision, note);
    }

    private static string Text(string value, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var result = value.Trim();
        return result.Length <= maximum
            ? result
            : throw new ArgumentException($"Value exceeds {maximum} characters.", nameof(value));
    }
}

public sealed record PreparedDocumentContent(string CatalogPath, string ContentHash, string Body);
