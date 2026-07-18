using System.Text.Json;

namespace Harness.Host.Realtime;

public sealed record RealtimeEventEnvelope(
    string Stream,
    long Sequence,
    string Type,
    DateTimeOffset OccurredAt,
    JsonElement Payload);
