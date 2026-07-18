using Microsoft.AspNetCore.SignalR;

namespace Harness.Host.Realtime;

public sealed class SignalRRealtimeEventBroadcaster(IHubContext<EventsHub> hubContext)
    : IRealtimeEventBroadcaster
{
    private readonly IHubContext<EventsHub> _hubContext =
        hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    public Task BroadcastAsync(
        RealtimeEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return _hubContext.Clients
            .Group(EventsHub.GetGroupName(envelope.Stream))
            .SendAsync("event", envelope, cancellationToken);
    }
}
