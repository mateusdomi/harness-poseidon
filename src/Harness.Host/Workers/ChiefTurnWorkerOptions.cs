namespace Harness.Host.Workers;

public sealed record ChiefTurnWorkerOptions(
    TimeSpan PollInterval,
    TimeSpan LeaseDuration);
