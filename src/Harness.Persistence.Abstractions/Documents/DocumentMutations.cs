using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Documents;

public enum DocumentMutationStatus
{
    Applied,
    IdempotentReplay,
    NotFound,
    VersionConflict,
    InvalidState,
}

public sealed record DocumentVersionAppendCommand(
    string TenantId,
    string DocumentId,
    string DocumentVersionId,
    string CatalogPath,
    string ContentHash,
    string AuthorKind,
    string? AuthorId,
    long ExpectedDocumentVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record DocumentMutationReceipt(
    DocumentMutationStatus Status,
    string DocumentId,
    long? DocumentVersion,
    string? State,
    int? CurrentVersion,
    string? DocumentVersionId = null,
    long? LedgerSequence = null,
    string? LedgerHash = null,
    string? OutboxMessageId = null);

public static class DocumentVersionAppendValidator
{
    private static readonly HashSet<string> AuthorKinds =
        new(StringComparer.Ordinal) { "user", "chief", "agent" };

    public static void Validate(DocumentVersionAppendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.TenantId, nameof(command));
        ValidateId(command.DocumentId, nameof(command));
        ValidateId(command.DocumentVersionId, nameof(command));
        ValidateCatalogPath(command.CatalogPath, nameof(command));
        ValidateContentHash(command.ContentHash, nameof(command));
        if (!AuthorKinds.Contains(command.AuthorKind))
        {
            throw new ArgumentException("Author kind is invalid.", nameof(command));
        }

        if (command.AuthorId is not null)
        {
            ValidateId(command.AuthorId, nameof(command));
        }

        if (command.ExpectedDocumentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Expected document version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey, nameof(command));
        if (command.IdempotencyKey.Length > 200 ||
            !string.Equals(
                command.IdempotencyKey,
                command.IdempotencyKey.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Idempotency key must be trimmed and no longer than 200 characters.",
                nameof(command));
        }

        if (command.OccurredAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Occurrence time is required.");
        }
    }

    public static string Hash(DocumentVersionAppendCommand command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));

    private static void ValidateCatalogPath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 1_024 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
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

    private static void ValidateId(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }
}
