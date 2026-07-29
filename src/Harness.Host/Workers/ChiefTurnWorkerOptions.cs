namespace Harness.Host.Workers;

public sealed record ChiefTurnWorkerOptions(
    TimeSpan PollInterval,
    TimeSpan LeaseDuration)
{
    public bool ContextBundlesEnabled { get; init; } = true;

    public int ContextTokenBudget { get; init; } = 12000;

    public TimeSpan ActivityHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
}
