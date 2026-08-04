using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Messaging;

public interface IOutboxStore
{
    Task<OutboxLease?> TryAcquireNextAsync(
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OutboxMutationResult> MarkDispatchedAsync(
        OutboxDispatchCommand command,
        CancellationToken cancellationToken = default);

    Task<OutboxMutationResult> RecordFailureAsync(
        OutboxFailureCommand command,
        CancellationToken cancellationToken = default);

    Task<int> ReleaseExpiredClaimsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OutboxStoreSnapshot> ReadSnapshotAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);
}

public sealed record OutboxLease(
    string MessageId,
    string TenantId,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    int Attempts,
    string Owner,
    long FencingToken,
    DateTimeOffset LockExpiresAt);

public sealed record OutboxDispatchCommand(
    string MessageId,
    string Owner,
    long FencingToken,
    DateTimeOffset DispatchedAt);

public sealed record OutboxFailureCommand(
    string MessageId,
    string FailureId,
    string Owner,
    long FencingToken,
    string Error,
    OutboxRetryPolicy RetryPolicy,
    DateTimeOffset FailedAt);

public enum OutboxMutationStatus
{
    Applied,
    NotFound,
    LeaseRejected,
    AlreadyTerminal,
    DeadLettered,
}

public sealed record OutboxMutationResult(
    OutboxMutationStatus Status,
    string MessageId,
    int? Attempts = null,
    DateTimeOffset? AvailableAt = null);

public sealed record OutboxStoreSnapshot(
    long Pending,
    long Claimed,
    long Dispatched,
    long DeadLettered,
    long FailureHistory)
{
    /// <summary>Idade da mensagem pendente mais antiga; nulo quando não há fila.</summary>
    public TimeSpan? OldestPendingAge { get; init; }

    /// <summary>Mensagens entregues no último minuto.</summary>
    public long DispatchedLastMinute { get; init; }

    /// <summary>Mensagens entregues na última hora.</summary>
    public long DispatchedLastHour { get; init; }
}

public sealed record OutboxRetryPolicy(
    int MaximumAttempts,
    TimeSpan InitialDelay,
    decimal Multiplier,
    TimeSpan MaximumDelay)
{
    public void Validate()
    {
        if (MaximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumAttempts),
                "Maximum attempts must be between 1 and 100.");
        }

        if (InitialDelay <= TimeSpan.Zero || InitialDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialDelay),
                "Initial delay must be positive and no longer than one day.");
        }

        if (Multiplier is < 1m or > 10m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Multiplier),
                "Multiplier must be between 1 and 10.");
        }

        if (MaximumDelay < InitialDelay || MaximumDelay > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDelay),
                "Maximum delay must be at least initial delay and no longer than seven days.");
        }
    }

    public TimeSpan DelayAfterFailure(int failureAttempt)
    {
        Validate();
        if (failureAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureAttempt),
                "Failure attempt must be positive.");
        }

        var delayTicks = (decimal)InitialDelay.Ticks;
        var maximumTicks = (decimal)MaximumDelay.Ticks;
        for (var attempt = 1; attempt < failureAttempt && delayTicks < maximumTicks; attempt++)
        {
            delayTicks = Math.Min(maximumTicks, delayTicks * Multiplier);
        }

        return TimeSpan.FromTicks(decimal.ToInt64(decimal.Truncate(delayTicks)));
    }
}

public static class OutboxContractValidator
{
    public static void ValidateAcquire(string owner, TimeSpan leaseDuration, DateTimeOffset now)
    {
        ValidateOwner(owner, nameof(owner));
        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Lease duration must be between one second and thirty minutes.");
        }

        ValidateTimestamp(now, nameof(now));
    }

    public static void Validate(OutboxDispatchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.MessageId, nameof(command));
        ValidateOwner(command.Owner, nameof(command));
        ValidateToken(command.FencingToken, nameof(command));
        ValidateTimestamp(command.DispatchedAt, nameof(command));
    }

    public static void Validate(OutboxFailureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.MessageId, nameof(command));
        ValidateId(command.FailureId, nameof(command));
        ValidateOwner(command.Owner, nameof(command));
        ValidateToken(command.FencingToken, nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Error, nameof(command));
        if (command.Error.Length > 4_000 ||
            !string.Equals(command.Error, command.Error.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Error must be trimmed and no longer than 4000 characters.",
                nameof(command));
        }

        ArgumentNullException.ThrowIfNull(command.RetryPolicy);
        command.RetryPolicy.Validate();
        ValidateTimestamp(command.FailedAt, nameof(command));
    }

    public static void ValidatePayload(string payloadJson, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson, parameterName);
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Outbox payload must be a JSON object.", parameterName);
        }
    }

    private static void ValidateOwner(string owner, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner, parameterName);
        if (owner.Length > 200 || !string.Equals(owner, owner.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Owner must be trimmed and no longer than 200 characters.",
                parameterName);
        }
    }

    private static void ValidateToken(long token, string parameterName)
    {
        if (token <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Fencing token must be positive.");
        }
    }

    private static void ValidateTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timestamp is required.");
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
