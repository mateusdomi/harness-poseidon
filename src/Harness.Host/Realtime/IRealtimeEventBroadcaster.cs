namespace Harness.Host.Realtime;

public interface IRealtimeEventBroadcaster
{
    Task BroadcastAsync(
        RealtimeEventEnvelope envelope,
        CancellationToken cancellationToken = default);
}
