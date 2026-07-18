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
