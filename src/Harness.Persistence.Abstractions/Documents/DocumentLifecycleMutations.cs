using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Documents;

public sealed record DocumentMetadataUpdateCommand(
    string TenantId,
    string DocumentId,
    IReadOnlyList<string> Classifications,
    string? PhaseName,
    bool Inconsistent,
    long ExpectedDocumentVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record DocumentTransitionCommand(
    string TenantId,
    string DocumentId,
    string TransitionId,
    string TargetState,
    string? Note,
    string ActorKind,
    string? ActorId,
    long ExpectedDocumentVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public static class DocumentLifecycleMutationValidator
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "planned",
        "in_elaboration",
        "in_review",
        "awaiting_approval",
        "approved",
        "outdated",
        "superseded",
        "not_applicable",
    };

    private static readonly HashSet<string> ActorKinds =
        new(StringComparer.Ordinal) { "user", "chief", "agent", "system" };

    public static void Validate(DocumentMetadataUpdateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.DocumentId,
            command.ExpectedDocumentVersion,
            command.IdempotencyKey,
            command.OccurredAt,
            nameof(command));
        ValidateClassifications(command.Classifications, nameof(command));
        ValidateOptionalText(command.PhaseName, 200, nameof(command));
    }

    public static void Validate(DocumentTransitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.DocumentId,
            command.ExpectedDocumentVersion,
            command.IdempotencyKey,
            command.OccurredAt,
            nameof(command));
        ValidateId(command.TransitionId, nameof(command));
        if (!States.Contains(command.TargetState))
        {
            throw new ArgumentException("Target state is invalid.", nameof(command));
        }

        ValidateOptionalText(command.Note, 10_000, nameof(command));
        if (!ActorKinds.Contains(command.ActorKind))
        {
            throw new ArgumentException("Actor kind is invalid.", nameof(command));
        }

        if (command.ActorKind == "system")
        {
            if (command.ActorId is not null)
            {
                throw new ArgumentException("A system transition cannot carry an actor id.", nameof(command));
            }
        }
        else if (command.ActorId is null)
        {
            throw new ArgumentException("A non-system transition requires an actor id.", nameof(command));
        }
        else
        {
            ValidateId(command.ActorId, nameof(command));
        }
    }

    public static string Hash(DocumentMetadataUpdateCommand command) => HashCore(command);

    public static string Hash(DocumentTransitionCommand command) => HashCore(command);

    private static string HashCore<T>(T command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));

    private static void ValidateClassifications(
        IReadOnlyList<string> classifications,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        if (classifications.Count > 50)
        {
            throw new ArgumentException("At most 50 classifications are allowed.", parameterName);
        }

        string? previous = null;
        foreach (var classification in classifications)
        {
            ValidateText(classification, 100, parameterName);
            if (previous is not null && string.CompareOrdinal(previous, classification) >= 0)
            {
                throw new ArgumentException(
                    "Classifications must be unique and ordinally sorted.",
                    parameterName);
            }

            previous = classification;
        }
    }

    private static void ValidateCommon(
        string tenantId,
        string documentId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string parameterName)
    {
        ValidateId(tenantId, parameterName);
        ValidateId(documentId, parameterName);
        if (expectedVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Expected document version must be positive.");
        }

        ValidateText(idempotencyKey, 200, parameterName);
        if (occurredAt == default)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Occurrence time is required.");
        }
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string parameterName)
    {
        if (value is not null)
        {
            ValidateText(value, maximumLength, parameterName);
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
