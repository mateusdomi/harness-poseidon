namespace Harness.Host.Workers;

public sealed record ChiefTurnWorkerOptions(
    TimeSpan PollInterval,
    TimeSpan LeaseDuration)
{
    public bool ContextBundlesEnabled { get; init; } = true;

    public int ContextTokenBudget { get; init; } = 12000;

    public TimeSpan ActivityHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fase 0A2 (BR-005): o lease é renovado PELO batimento, então ele precisa durar bem mais que o
    /// intervalo entre batimentos. Com a folga apertada, uma renovação atrasada por uma escrita
    /// lenta deixaria o turno vivo parecer abandonado — e outro worker o reexecutaria, dobrando o
    /// custo. Exijo pelo menos o triplo: sobram duas tentativas antes de o lease vencer.
    /// </summary>
    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval), "The poll interval must be positive.");
        }

        if (ActivityHeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ActivityHeartbeatInterval), "The heartbeat interval must be positive.");
        }

        if (LeaseDuration < ActivityHeartbeatInterval * 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseDuration),
                "The turn lease must last at least three heartbeat intervals so a delayed renewal never orphans a live turn.");
        }
    }
}
