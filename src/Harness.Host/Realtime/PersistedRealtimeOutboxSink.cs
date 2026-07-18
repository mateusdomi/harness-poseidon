using System.Text.Json;
using Harness.Host.Workers;
using Harness.Persistence.Abstractions.Messaging;
using Harness.Persistence.Abstractions.Realtime;

namespace Harness.Host.Realtime;

public sealed class PersistedRealtimeOutboxSink(
    IRealtimeEventStore store,
    OutboxRealtimeStreamResolver streamResolver,
    IRealtimeEventBroadcaster broadcaster) : IOutboxMessageSink
{
    private readonly IRealtimeEventStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly OutboxRealtimeStreamResolver _streamResolver =
        streamResolver ?? throw new ArgumentNullException(nameof(streamResolver));
    private readonly IRealtimeEventBroadcaster _broadcaster =
        broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));

    public async Task DispatchAsync(
        OutboxLease message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!EventTypeCatalog.All.Contains(message.EventType))
        {
            throw new ArgumentException(
                "Outbox event type is not part of the realtime catalog.",
                nameof(message));
        }

        var receipt = await _store.AppendAsync(
            new RealtimeEventAppendCommand(
                message.MessageId,
                message.TenantId,
                _streamResolver.Resolve(message),
                message.EventType,
                message.PayloadJson,
                message.OccurredAt),
            cancellationToken);
        if (receipt.Replay)
        {
            return;
        }

        using var payload = JsonDocument.Parse(receipt.PayloadJson);
        await _broadcaster.BroadcastAsync(
            new RealtimeEventEnvelope(
                receipt.Stream,
                receipt.Sequence,
                receipt.EventType,
                receipt.OccurredAt,
                payload.RootElement.Clone()),
            cancellationToken);
    }
}
