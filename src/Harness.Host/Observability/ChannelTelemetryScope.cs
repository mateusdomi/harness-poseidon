using System.Diagnostics;

namespace Harness.Host.Observability;

internal sealed class ChannelTelemetryScope : IDisposable
{
    private readonly string _channel;
    private readonly string _direction;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly Activity? _activity;
    private bool _recorded;

    internal ChannelTelemetryScope(string channel, string direction)
    {
        _channel = channel;
        _direction = direction;
        _activity = PoseidonTelemetry.ActivitySource.StartActivity(
            $"poseidon.channel.{direction}",
            direction == "inbound" ? ActivityKind.Consumer : ActivityKind.Producer);
        _activity?.SetTag("messaging.system", channel);
        _activity?.SetTag("channel.direction", direction);
    }

    internal void SetCorrelation(
        string tenantId,
        string projectId,
        string conversationId,
        string? turnId,
        string? linkId,
        string? messageId = null)
    {
        _activity?.SetTag("tenant_id", tenantId);
        _activity?.SetTag("project_id", projectId);
        _activity?.SetTag("conversation_id", conversationId);
        _activity?.SetTag("chief_turn_id", turnId);
        _activity?.SetTag("channel_link_id", linkId);
        _activity?.SetTag("message_id", messageId);
    }

    internal void Complete(string result)
    {
        if (_recorded)
        {
            return;
        }

        _recorded = true;
        _activity?.SetTag("channel.result", result);
        PoseidonTelemetry.RecordChannelOperation(
            _channel,
            _direction,
            result,
            Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _activity?.SetStatus(ActivityStatusCode.Error);
        _activity?.SetTag("error.type", exception.GetType().FullName);
        Complete("failed");
    }

    public void Dispose()
    {
        if (!_recorded)
        {
            Complete("abandoned");
        }

        _activity?.Dispose();
    }
}
