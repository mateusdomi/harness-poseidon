namespace Harness.Host.Governance;

public sealed record GovernanceFeatureSettings
{
    public bool ContextBundlesEnabled { get; init; } = true;

    /// <summary>
    /// Orçamento do bundle documental. Subiu de 12.000 quando a seleção passou a usar o
    /// vocabulário real do trabalho: o conjunto correto de documentos é maior que o conjunto
    /// que a incompatibilidade de vocabulário deixava passar. A política de faixas garante que o
    /// obrigatório nunca caia; o orçamento maior evita que o opcional caia por engano.
    /// </summary>
    public int ContextTokenBudget { get; init; } = 16000;

    public bool HashlinePatchesEnabled { get; init; } = true;

    public bool StaleDocumentDetectorEnabled { get; init; } = true;

    public bool LearningCandidatesEnabled { get; init; } = true;

    // PLAT-02: estratégia de contexto do Chief. Fase 1E: LIGADA por padrão.
    //
    // Ela nasceu desligada como default seguro, e o "temporário" durou até a estratégia estar
    // completa e testada e ainda assim não governar nada — a mesma doença do EffortPolicy e do
    // ExecutionCheckpointService. Desligada, o Chief lê o histórico sem limiar e sem externalizar
    // fato crítico: em projeto longo isso é a janela estourando ou a decisão recente sumindo.
    // Depois do 0A2 (últimas N + fundadora preservada + notas recuperadas com proveniência), o
    // comportamento LIGADO é o correto, e mantê-la desligada seria preservar o defeito.
    public bool ChiefContextStrategyEnabled { get; init; } = true;

    public int ChiefContextMaxTokens { get; init; } = 12000;

    public int ChiefContextRecentTurns { get; init; } = 12;

    public int ChiefContextToolResultWindow { get; init; } = 4;

    public int ChiefContextHistoryScanLimit { get; init; } = 200;
}
