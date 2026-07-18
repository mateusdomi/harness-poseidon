using Microsoft.AspNetCore.SignalR;

namespace Harness.Host.Realtime;

public sealed class EventsHub : Hub
{
    public async Task<EventSubscriptionAck> Subscribe(IReadOnlyList<string> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var normalized = streams.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0 || normalized.Any(stream => !EventStreamName.IsValid(stream)))
        {
            throw new HubException("invalid_event_stream");
        }

        foreach (var stream in normalized)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupName(stream), Context.ConnectionAborted);
        }

        return new EventSubscriptionAck(normalized);
    }

    public async Task Unsubscribe(IReadOnlyList<string> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        foreach (var stream in streams.Where(EventStreamName.IsValid).Distinct(StringComparer.Ordinal))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupName(stream), Context.ConnectionAborted);
        }
    }

    internal static string GetGroupName(string stream) => $"stream::{stream}";
}

public sealed record EventSubscriptionAck(IReadOnlyList<string> Streams);
