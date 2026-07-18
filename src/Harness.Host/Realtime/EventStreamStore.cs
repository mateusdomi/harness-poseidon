using System.Text.Json;

namespace Harness.Host.Realtime;

public sealed class EventStreamStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<RealtimeEventEnvelope>> _events = new(StringComparer.Ordinal);

    public RealtimeEventEnvelope Append(
        string stream,
        string type,
        DateTimeOffset occurredAt,
        JsonElement payload)
    {
        if (!EventStreamName.IsValid(stream))
        {
            throw new ArgumentException("A valid event stream is required.", nameof(stream));
        }

        if (!EventTypeCatalog.All.Contains(type))
        {
            throw new ArgumentException("The event type is not part of the canonical catalog.", nameof(type));
        }

        if (payload.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("An event payload must be a JSON object.", nameof(payload));
        }

        lock (_sync)
        {
            if (!_events.TryGetValue(stream, out var streamEvents))
            {
                streamEvents = [];
                _events.Add(stream, streamEvents);
            }

            var envelope = new RealtimeEventEnvelope(
                stream,
                streamEvents.Count + 1L,
                type,
                occurredAt.ToUniversalTime(),
                payload.Clone());
            streamEvents.Add(envelope);
            return envelope;
        }
    }

    public EventStreamSnapshot ReadSnapshot(string stream, long afterSequence)
    {
        if (!EventStreamName.IsValid(stream))
        {
            throw new ArgumentException("A valid event stream is required.", nameof(stream));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        lock (_sync)
        {
            if (!_events.TryGetValue(stream, out var streamEvents))
            {
                return new EventStreamSnapshot(
                    stream,
                    0,
                    new SortedDictionary<string, RealtimeEventEnvelope>(StringComparer.Ordinal),
                    []);
            }

            var latestByType = streamEvents
                .GroupBy(item => item.Type, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.Ordinal);
            var delta = streamEvents.Where(item => item.Sequence > afterSequence).ToArray();
            return new EventStreamSnapshot(stream, streamEvents.Count, latestByType, delta);
        }
    }
}
