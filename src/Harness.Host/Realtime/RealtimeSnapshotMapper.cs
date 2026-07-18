using System.Text.Json;
using Harness.Persistence.Abstractions.Realtime;

namespace Harness.Host.Realtime;

public static class RealtimeSnapshotMapper
{
    public static EventStreamSnapshot ToContract(RealtimeEventStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var latest = snapshot.LatestByType.ToDictionary(
            item => item.Key,
            item => ToEnvelope(item.Value),
            StringComparer.Ordinal);
        var delta = snapshot.Delta.Select(ToEnvelope).ToArray();
        return new EventStreamSnapshot(snapshot.Stream, snapshot.Sequence, latest, delta);
    }

    private static RealtimeEventEnvelope ToEnvelope(RealtimeStoredEvent storedEvent)
    {
        using var payload = JsonDocument.Parse(storedEvent.PayloadJson);
        return new RealtimeEventEnvelope(
            storedEvent.Stream,
            storedEvent.Sequence,
            storedEvent.EventType,
            storedEvent.OccurredAt,
            payload.RootElement.Clone());
    }
}
