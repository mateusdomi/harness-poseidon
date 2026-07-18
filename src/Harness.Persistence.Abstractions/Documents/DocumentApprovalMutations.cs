using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Documents;

public sealed record DocumentApprovalRequestCommand(
    string TenantId,
    string DocumentId,
    string ApprovalRequestId,
    string TransitionId,
    string Title,
    string Description,
    string Priority,
    DateTimeOffset? DueAt,
    string RequestedByAgentId,
    long ExpectedDocumentVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record DocumentApprovalResolveCommand(
    string TenantId,
    string DocumentId,
    string ApprovalRequestId,
    string TransitionId,
    string Decision,
    string ResolvedByProfileId,
    string? Note,
    long ExpectedDocumentVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record DocumentApprovalCancelCommand(
    string TenantId,
    string DocumentId,
    string ApprovalRequestId,
    string TransitionId,
    string Reason,
    string ActorKind,
    string? ActorId,
    long ExpectedDocumentVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public static class DocumentApprovalMutationValidator
{
    private static readonly HashSet<string> Priorities =
        new(StringComparer.Ordinal) { "low", "medium", "high", "critical" };

    private static readonly HashSet<string> Decisions =
        new(StringComparer.Ordinal) { "approved", "rejected" };

    private static readonly HashSet<string> ActorKinds =
        new(StringComparer.Ordinal) { "user", "chief", "agent", "system" };

    public static void Validate(DocumentApprovalRequestCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.DocumentId,
            command.ApprovalRequestId,
            command.TransitionId,
            command.ExpectedDocumentVersion,
            command.IdempotencyKey,
            command.OccurredAt,
            nameof(command));
        ValidateText(command.Title, 500, nameof(command));
        ValidateText(command.Description, 10_000, nameof(command));
        if (!Priorities.Contains(command.Priority))
        {
            throw new ArgumentException("Approval priority is invalid.", nameof(command));
        }

        if (command.DueAt is not null && command.DueAt <= command.OccurredAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Approval due date must follow occurrence time.");
        }

        ValidateId(command.RequestedByAgentId, nameof(command));
    }

    public static void Validate(DocumentApprovalResolveCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.DocumentId,
            command.ApprovalRequestId,
            command.TransitionId,
            command.ExpectedDocumentVersion,
            command.IdempotencyKey,
            command.OccurredAt,
            nameof(command));
        if (!Decisions.Contains(command.Decision))
        {
            throw new ArgumentException("Approval decision is invalid.", nameof(command));
        }

        ValidateId(command.ResolvedByProfileId, nameof(command));
        ValidateOptionalText(command.Note, 10_000, nameof(command));
    }

    public static void Validate(DocumentApprovalCancelCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.DocumentId,
            command.ApprovalRequestId,
            command.TransitionId,
            command.ExpectedDocumentVersion,
            command.IdempotencyKey,
            command.OccurredAt,
            nameof(command));
        ValidateText(command.Reason, 10_000, nameof(command));
        ValidateActor(command.ActorKind, command.ActorId, nameof(command));
    }

    public static string Hash(DocumentApprovalRequestCommand command) => HashCore(command);

    public static string Hash(DocumentApprovalResolveCommand command) => HashCore(command);

    public static string Hash(DocumentApprovalCancelCommand command) => HashCore(command);

    private static string HashCore<T>(T command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));

    private static void ValidateCommon(
        string tenantId,
        string documentId,
        string approvalRequestId,
        string transitionId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string parameterName)
    {
        ValidateId(tenantId, parameterName);
        ValidateId(documentId, parameterName);
        ValidateId(approvalRequestId, parameterName);
        ValidateId(transitionId, parameterName);
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

    private static void ValidateActor(string kind, string? id, string parameterName)
    {
        if (!ActorKinds.Contains(kind))
        {
            throw new ArgumentException("Actor kind is invalid.", parameterName);
        }

        if (kind == "system")
        {
            if (id is not null)
            {
                throw new ArgumentException("System actor cannot carry an id.", parameterName);
            }
        }
        else if (id is null)
        {
            throw new ArgumentException("Non-system actor requires an id.", parameterName);
        }
        else
        {
            ValidateId(id, parameterName);
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
