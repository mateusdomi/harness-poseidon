namespace Harness.Host.Governance;

public sealed record GovernanceFeatureSettings
{
    public bool ContextBundlesEnabled { get; init; } = true;

    public int ContextTokenBudget { get; init; } = 12000;

    public bool HashlinePatchesEnabled { get; init; } = true;

    public bool StaleDocumentDetectorEnabled { get; init; } = true;

    public bool LearningCandidatesEnabled { get; init; } = true;

    // PLAT-02: estratégia de contexto do Chief. Default seguro DESLIGADA — quando desligada, o Chief
    // mantém o comportamento anterior. Quando ligada, a janela é limitada por limiar e os fatos
    // críticos são externalizados como notas duráveis.
    public bool ChiefContextStrategyEnabled { get; init; }

    public int ChiefContextMaxTokens { get; init; } = 12000;

    public int ChiefContextRecentTurns { get; init; } = 12;

    public int ChiefContextToolResultWindow { get; init; } = 4;

    public int ChiefContextHistoryScanLimit { get; init; } = 200;
}
