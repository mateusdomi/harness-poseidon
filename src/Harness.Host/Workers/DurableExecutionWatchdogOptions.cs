namespace Harness.Host.Workers;

public sealed record DurableExecutionWatchdogOptions(
    TimeSpan PollInterval,
    TimeSpan HeartbeatTimeout)
{
    public void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(10) ||
            PollInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval),
                "Poll interval must be between 10 milliseconds and one minute.");
        }

        if (HeartbeatTimeout < TimeSpan.FromSeconds(1) ||
            HeartbeatTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatTimeout),
                "Heartbeat timeout must be between one second and one day.");
        }
    }
}
