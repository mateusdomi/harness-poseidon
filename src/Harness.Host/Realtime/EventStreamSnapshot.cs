namespace Harness.Host.Realtime;

public sealed record EventStreamSnapshot(
    string Stream,
    long Sequence,
    IReadOnlyDictionary<string, RealtimeEventEnvelope> LatestByType,
    IReadOnlyList<RealtimeEventEnvelope> Delta);
