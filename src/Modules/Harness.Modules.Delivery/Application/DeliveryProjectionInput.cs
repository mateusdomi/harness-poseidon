namespace Harness.Modules.Delivery.Application;

/// <summary>
/// Fatos PUROS de uma entrega já coletados das fontes reusadas (Projects, Coordination, Documents,
/// Governance) — sem IO. É a entrada única dos projetores DEL-01/DEL-02. Nenhum campo é inventado:
/// cada um mapeia para um registro persistido. O Host materializa este input a partir dos stores.
/// </summary>
public sealed record DeliveryProjectionInput(
    DeliveryProjectFacts Project,
    DateTimeOffset AsOf,
    IReadOnlyList<DeliverySolicitationFacts> Solicitations,
    IReadOnlyList<DeliveryDemandFacts> Demands,
    IReadOnlyList<DeliveryTaskFacts> Tasks,
    IReadOnlyList<DeliveryAttemptFacts> Attempts,
    IReadOnlyList<DeliveryDocumentFacts> Documents,
    int StuckTaskCount,
    IReadOnlyList<DeliveryFeatureMetricFacts> FeatureMetrics,
    IReadOnlyList<DeliveryStoredForecast> ForecastHistory);

public sealed record DeliveryProjectFacts(
    string ProjectId, string Name, string Key, string Criticality, string ChiefAgentId,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);

public sealed record DeliverySolicitationFacts(
    string Id, string State, string? SupersedesId, DateTimeOffset CreatedAt);

public sealed record DeliveryDemandFacts(
    string Id, string State, DateTimeOffset CreatedAt);

/// <summary>
/// Um card do board (não confundir "entrega" com "card"). Estados espelham o board:
/// backlog/ready/development/review/corrections/testsGates/blocked/done. CardType inclui
/// 'agent_task','human_gate','spike','decision','feature'.
/// </summary>
public sealed record DeliveryTaskFacts(
    string Id, string? DemandId, string State, string CardType, string? AssigneeAgentId,
    string? BlockedReason, DateTimeOffset? DueAt, DateTimeOffset UpdatedAt);

public sealed record DeliveryAttemptFacts(
    string TaskId, int Number, string State, string? FailureReason, string? InstructionHash);

public sealed record DeliveryDocumentFacts(string Kind, string State);

public sealed record DeliveryFeatureMetricFacts(
    string FeatureId, int TaskCount, int AttemptCount, int SuccessCount, int FailureCount,
    decimal TotalCostUsd, long TotalTokensInput, long TotalTokensOutput, long TotalDurationMs);

/// <summary>Uma previsão gravada no histórico append-only (DEL-09), do mais recente ao mais antigo.</summary>
public sealed record DeliveryStoredForecast(
    string Id, DateTimeOffset? ForecastDate, string Confidence, int ConfidencePercent,
    bool HasSufficientEvidence, IReadOnlyList<DeliveryStoredForecastBasis> Basis,
    DateTimeOffset CreatedAt);

public sealed record DeliveryStoredForecastBasis(string Signal, string Detail);
