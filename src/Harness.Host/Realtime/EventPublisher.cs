using System.Text.Json;
using Harness.Persistence.Abstractions.Realtime;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Realtime;

public sealed class EventPublisher(
    IRealtimeEventStore store,
    IRealtimeEventBroadcaster broadcaster,
    IClock clock)
{
    public async Task<RealtimeEventEnvelope> PublishAsync(
        string tenantId,
        string stream,
        string type,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!EventTypeCatalog.All.Contains(type))
        {
            throw new ArgumentException(
                "The event type is not part of the canonical catalog.",
                nameof(type));
        }

        var payloadElement = JsonSerializer.SerializeToElement(payload);
        var occurredAt = clock.UtcNow;
        var receipt = await store.AppendAsync(
            new RealtimeEventAppendCommand(
                UlidValue.New(occurredAt).ToString(),
                tenantId,
                stream,
                type,
                payloadElement.GetRawText(),
                occurredAt),
            cancellationToken);
        var envelope = new RealtimeEventEnvelope(
            receipt.Stream,
            receipt.Sequence,
            receipt.EventType,
            receipt.OccurredAt,
            payloadElement);
        if (!receipt.Replay)
        {
            await broadcaster.BroadcastAsync(envelope, cancellationToken);
        }

        return envelope;
    }
}
