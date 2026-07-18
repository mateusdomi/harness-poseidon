using System.Text.Json;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.SignalR;

namespace Harness.Host.Realtime;

public sealed class EventPublisher(
    EventStreamStore store,
    IHubContext<EventsHub> hubContext,
    IClock clock)
{
    public async Task<RealtimeEventEnvelope> PublishAsync(
        string stream,
        string type,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var payloadElement = JsonSerializer.SerializeToElement(payload);
        var envelope = store.Append(stream, type, clock.UtcNow, payloadElement);
        await hubContext.Clients
            .Group(EventsHub.GetGroupName(stream))
            .SendAsync("event", envelope, cancellationToken);
        return envelope;
    }
}
