namespace Harness.Host.Governance;

public sealed record GovernanceFeatureSettings
{
    public bool ContextBundlesEnabled { get; init; } = true;

    public int ContextTokenBudget { get; init; } = 12000;

    public bool HashlinePatchesEnabled { get; init; } = true;

    public bool StaleDocumentDetectorEnabled { get; init; } = true;
}
