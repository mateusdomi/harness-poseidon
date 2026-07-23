namespace Harness.Modules.Delivery.Contracts;

// Central de Entregas (Tech Lead). Uma "entrega" (delivery) é derivada de um PROJETO — não é um card
// de dev. Os contratos abaixo são READ-MODELS: agregam Coordination/Projects/Documents/Governance
// sem duplicar seus dados. Todos os números derivam estritamente de fatos gravados; nada é inventado.

// ---------------------------------------------------------------------------------------------------
// DEL-01 — Portfólio de Entregas
// ---------------------------------------------------------------------------------------------------

/// <summary>Um sinal tipado de "precisa da minha atenção" derivado de dados existentes.</summary>
public sealed record AttentionSignalContract(string Code, string Severity, string Detail);

/// <summary>
/// Uma entrega no portfólio (uma por projeto). Saúde (green/yellow/red) e previsibilidade
/// (on_track/at_risk/off_track/unknown) derivam dos fatos; a previsão embutida é honesta (DEL-09).
/// </summary>
public sealed record DeliverySummaryContract(
    string DeliveryId,
    string ProjectId,
    string Name,
    string Key,
    string Health,
    string Predictability,
    string? Owner,
    DateTimeOffset? CommittedDate,
    DateTimeOffset? ForecastDate,
    string? ForecastConfidence,
    int MilestonesTotal,
    int MilestonesDone,
    int OpenTaskCount,
    int BlockedTaskCount,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<AttentionSignalContract> AttentionSignals);

public sealed record DeliveryPortfolioContract(
    string View, int Total, IReadOnlyList<DeliverySummaryContract> Deliveries);

// ---------------------------------------------------------------------------------------------------
// DEL-09 — Previsão honesta
// ---------------------------------------------------------------------------------------------------

public sealed record ForecastBasisContract(string Signal, string Detail);

/// <summary>
/// A previsão honesta: NUNCA inventa data. Quando a evidência é insuficiente,
/// <see cref="ForecastDate"/> é nulo, a confiança é 'low' e a base explica o porquê.
/// <see cref="ConfidencePercent"/> é um SCORE grosseiro derivado (não uma probabilidade).
/// </summary>
public sealed record DeliveryForecastContract(
    string? Id,
    DateTimeOffset? ForecastDate,
    string Confidence,
    int ConfidencePercent,
    bool HasSufficientEvidence,
    IReadOnlyList<ForecastBasisContract> Basis,
    DateTimeOffset? CreatedAt);

/// <summary>
/// Histórico APPEND-ONLY de previsões (nunca sobrescrita silenciosa). A previsão mais recente vem
/// primeiro; cada linha é uma nova previsão auditável.
/// </summary>
public sealed record DeliveryForecastHistoryContract(
    string DeliveryId,
    DeliveryForecastContract? Latest,
    IReadOnlyList<DeliveryForecastContract> History);

// ---------------------------------------------------------------------------------------------------
// DEL-02 — Projeto 360
// ---------------------------------------------------------------------------------------------------

public sealed record DeliveryExecutiveSummaryContract(
    string Name,
    string Key,
    string Criticality,
    string Health,
    string Predictability,
    string? Owner,
    int MilestonesTotal,
    int MilestonesDone,
    int OpenTaskCount,
    int BlockedTaskCount,
    DateTimeOffset? CommittedDate,
    DateTimeOffset? ForecastDate,
    DateTimeOffset LastActivityAt,
    int AttentionSignalCount);

public sealed record DeliveryPlanMilestonesContract(
    int MilestonesTotal,
    int MilestonesDone,
    DateTimeOffset? CommittedDate,
    DeliveryForecastContract Forecast,
    IReadOnlyList<DeliveryForecastContract> ForecastHistory);

/// <summary>Um indicador de saúde técnica. Status ∈ good/watch/bad/unknown; valor derivado dos dados.</summary>
public sealed record TechnicalHealthIndicatorContract(
    string Key, string Label, string Status, string Value, string Detail);

public sealed record DeliveryTechnicalHealthContract(
    IReadOnlyList<TechnicalHealthIndicatorContract> Indicators);

public sealed record DeliveryRiskDependencyContract(
    IReadOnlyList<AttentionSignalContract> Risks,
    int OpenDependencies,
    int BlockedTaskCount);

public sealed record DeliveryDecisionContract(
    string TaskId, string State, string Detail, bool Resolved);

public sealed record DeliveryDecisionsContract(
    int Total, int Open, IReadOnlyList<DeliveryDecisionContract> Decisions);

public sealed record DocumentationChecklistItemContract(
    string Kind, string Label, bool Present, string? State);

public sealed record DeliveryDocumentationContract(
    int Expected, int Present, IReadOnlyList<DocumentationChecklistItemContract> Checklist);

public sealed record DeliveryFeatureMetricContract(
    string FeatureId, int TaskCount, int AttemptCount, int SuccessCount, int FailureCount,
    decimal TotalCostUsd, long TotalTokensInput, long TotalTokensOutput, long TotalDurationMs);

public sealed record DeliveryValueMetricsContract(
    IReadOnlyList<DeliveryFeatureMetricContract> Features,
    decimal TotalCostUsd,
    int TotalTasks);

public sealed record Delivery360Contract(
    string DeliveryId,
    string ProjectId,
    DeliveryExecutiveSummaryContract ExecutiveSummary,
    DeliveryPlanMilestonesContract PlanAndMilestones,
    DeliveryTechnicalHealthContract TechnicalHealth,
    DeliveryRiskDependencyContract RisksAndDependencies,
    DeliveryDecisionsContract Decisions,
    DeliveryDocumentationContract Documentation,
    DeliveryValueMetricsContract ValueAndMetrics);
