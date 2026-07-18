using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Documents;

public interface IDocumentStore
{
    Task<DocumentCreateReceipt> CreateAsync(
        DocumentCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<DocumentStoreSnapshot?> ReadAsync(
        string tenantId,
        string documentId,
        CancellationToken cancellationToken = default);
}

public sealed record DocumentCreateCommand(
    string TenantId,
    string ProjectId,
    string DocumentId,
    string Title,
    string Kind,
    IReadOnlyList<string> Classifications,
    string? PhaseName,
    string DocumentVersionId,
    string CatalogPath,
    string ContentHash,
    string AuthorKind,
    string? AuthorId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record DocumentCreateReceipt(
    string DocumentId,
    string DocumentVersionId,
    long DocumentVersion,
    long LedgerSequence,
    string LedgerHash,
    string OutboxMessageId,
    bool Replay);

public sealed record DocumentStoreSnapshot(
    string TenantId,
    string ProjectId,
    string DocumentId,
    string Title,
    string Kind,
    string State,
    int CurrentVersion,
    string? PhaseName,
    bool Inconsistent,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Classifications,
    IReadOnlyList<DocumentVersionStoreSnapshot> Versions,
    IReadOnlyList<DocumentApprovalStoreSnapshot> ApprovalRequests,
    IReadOnlyList<DocumentTransitionStoreSnapshot> StateTransitions);

public sealed record DocumentVersionStoreSnapshot(
    string DocumentVersionId,
    int Version,
    string CatalogPath,
    string ContentHash,
    string? SupersedesId,
    string AuthorKind,
    string? AuthorId,
    DateTimeOffset CreatedAt);

public sealed record DocumentApprovalStoreSnapshot(
    string ApprovalRequestId,
    string DocumentVersionId,
    string Title,
    string Description,
    string Priority,
    DateTimeOffset? DueAt,
    string State,
    string RequestedByAgentId,
    DateTimeOffset RequestedAt,
    string? ResolvedByProfileId,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote,
    long Version);

public sealed record DocumentTransitionStoreSnapshot(
    string TransitionId,
    long DocumentVersion,
    string FromState,
    string ToState,
    string? Note,
    string ActorKind,
    string? ActorId,
    DateTimeOffset OccurredAt);

public static class DocumentCreateValidator
{
    private static readonly HashSet<string> Kinds =
        new(StringComparer.Ordinal) { "prd", "spec", "design", "runbook", "note", "report" };

    private static readonly HashSet<string> AuthorKinds =
        new(StringComparer.Ordinal) { "user", "chief", "agent" };

    public static void Validate(DocumentCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.TenantId, nameof(command));
        ValidateId(command.ProjectId, nameof(command));
        ValidateId(command.DocumentId, nameof(command));
        ValidateId(command.DocumentVersionId, nameof(command));
        ValidateText(command.Title, 500, nameof(command));
        ValidateSet(command.Kind, Kinds, nameof(command));
        ValidateClassifications(command.Classifications, nameof(command));
        ValidateOptionalText(command.PhaseName, 200, nameof(command));
        ValidateCatalogPath(command.CatalogPath, nameof(command));
        ValidateContentHash(command.ContentHash, nameof(command));
        ValidateSet(command.AuthorKind, AuthorKinds, nameof(command));
        if (command.AuthorId is not null)
        {
            ValidateId(command.AuthorId, nameof(command));
        }

        ValidateText(command.IdempotencyKey, 200, nameof(command));
        if (command.OccurredAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Occurrence time is required.");
        }
    }

    private static void ValidateClassifications(
        IReadOnlyList<string> classifications,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        if (classifications.Count > 50)
        {
            throw new ArgumentException("At most 50 classifications are allowed.", parameterName);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var classification in classifications)
        {
            ValidateText(classification, 100, parameterName);
            if (!string.Equals(classification, classification.Trim(), StringComparison.Ordinal) ||
                !seen.Add(classification) ||
                (previous is not null && string.CompareOrdinal(previous, classification) >= 0))
            {
                throw new ArgumentException(
                    "Classifications must be trimmed, unique, and ordinally sorted.",
                    parameterName);
            }

            previous = classification;
        }
    }

    private static void ValidateCatalogPath(string value, string parameterName)
    {
        ValidateText(value, 1_024, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            Path.IsPathRooted(value) ||
            value.Contains('\\') ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Catalog path must be a canonical relative path.", parameterName);
        }
    }

    private static void ValidateContentHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsLower(character)))
        {
            throw new ArgumentException(
                "Content hash must be a canonical SHA-256 hex value.",
                parameterName);
        }
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        ValidateText(value, maximumLength, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Optional text must be trimmed.", parameterName);
        }
    }

    private static void ValidateSet(
        string value,
        HashSet<string> allowed,
        string parameterName)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException("Value is outside the supported catalog.", parameterName);
        }
    }

    private static void ValidateText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Text must be trimmed and no longer than {maximumLength} characters.",
                parameterName);
        }
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }
}

public static class DocumentCreateHash
{
    public static string Compute(DocumentCreateCommand command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
}
