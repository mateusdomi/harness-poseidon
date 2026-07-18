using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Realtime;

public interface IRealtimeEventStore
{
    Task<RealtimeEventAppendReceipt> AppendAsync(
        RealtimeEventAppendCommand command,
        CancellationToken cancellationToken = default);

    Task<RealtimeEventStoreSnapshot> ReadSnapshotAsync(
        string stream,
        long afterSequence,
        CancellationToken cancellationToken = default);
}

public sealed record RealtimeEventAppendCommand(
    string MessageId,
    string TenantId,
    string Stream,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt);

public sealed record RealtimeEventAppendReceipt(
    string MessageId,
    string Stream,
    long Sequence,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    bool Replay);

public sealed record RealtimeStoredEvent(
    string MessageId,
    string Stream,
    long Sequence,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt);

public sealed record RealtimeEventStoreSnapshot(
    string Stream,
    long Sequence,
    IReadOnlyDictionary<string, RealtimeStoredEvent> LatestByType,
    IReadOnlyList<RealtimeStoredEvent> Delta);

public static class RealtimeEventContractValidator
{
    public static void Validate(RealtimeEventAppendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUlid(command.MessageId, nameof(command.MessageId));
        ValidateUlid(command.TenantId, nameof(command.TenantId));
        ValidateStream(command.Stream);
        ValidateEventType(command.EventType);
        ValidatePayload(command.PayloadJson);
        if (command.OccurredAt == default || command.OccurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "OccurredAt must be a non-default UTC timestamp.");
        }
    }

    public static void ValidateRead(string stream, long afterSequence)
    {
        ValidateStream(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
    }

    public static string CanonicalizePayload(string payloadJson)
    {
        ValidatePayload(payloadJson);
        return AuditLedgerHash.CanonicalizeJson(payloadJson);
    }

    private static void ValidateUlid(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("A canonical ULID is required.", parameterName);
        }
    }

    private static void ValidateStream(string stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        if (stream.Length > 200 ||
            stream.IndexOf(':', StringComparison.Ordinal) <= 0 ||
            stream.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Stream must contain a namespace separator, contain no whitespace, and be at most 200 characters.",
                nameof(stream));
        }
    }

    private static void ValidateEventType(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventType.Length > 200 ||
            !string.Equals(eventType, eventType.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Event type must be trimmed and at most 200 characters.",
                nameof(eventType));
        }
    }

    private static void ValidatePayload(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new ArgumentException("Realtime payload must be a JSON object.", nameof(payloadJson));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Realtime payload must be valid JSON.", nameof(payloadJson), exception);
        }
    }
}
