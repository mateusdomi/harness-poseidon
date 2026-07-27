using Harness.SharedKernel.Providers;

namespace Harness.Modules.Providers.Contracts;

/// <summary>
/// Solicitação de roteamento de modelo (Fase 3).
/// </summary>
public sealed record ModelRoutingRequest(
    string Role,
    string RequiredCapability,
    string? PreferredModel,
    string? RiskTier,
    string? ActorAlias,
    bool ForCritic,
    IReadOnlyList<string> RequiredPathScopes,
    DateTimeOffset Now);

/// <summary>
/// Seleção de conta já concluída pelo scheduler de agentes. O Model Router não reavalia
/// autorização, escopo, autenticação, concorrência ou independência actor/critic: recebe a
/// decisão autoritativa e apenas materializa provedor/modelo e fallback para execução/auditoria.
/// </summary>
public sealed record ScheduledAccountSelection(
    string? SelectedAlias,
    string ReasonCode,
    IReadOnlyList<ScheduledAccountCandidate> Candidates,
    IReadOnlyList<string> FallbackAliases);

public sealed record ScheduledAccountCandidate(
    string Alias,
    bool Eligible,
    string ReasonCode,
    int Priority);

/// <summary>
/// Decisão de roteamento de modelo com rastreabilidade determinística.
/// </summary>
public sealed record ModelRoutingDecision(
    string? SelectedAlias,
    string? SelectedModel,
    string? Provider,
    bool IsFallback,
    string DecisionReason,
    IReadOnlyList<string> EvaluatedCandidates,
    DateTimeOffset RoutedAt);

/// <summary>
/// Snapshot de cota por conta para Capacity Manager (Fase 3).
/// </summary>
public sealed record QuotaStatusRecord(
    string Source,
    DateTimeOffset ObservedAt,
    string Status,
    string Confidence,
    double? RemainingFraction,
    DateTimeOffset? ResetAt,
    TimeSpan StaleAfter,
    string? OverrideReason = null);

/// <summary>
/// Snapshot de capacidade consolidada da frota de provedores.
/// </summary>
public sealed record CapacityStatusSnapshot(
    IReadOnlyDictionary<string, QuotaStatusRecord> AccountQuotas,
    IReadOnlyDictionary<string, int> ConsecutiveFailures,
    bool IsUnderBackpressure,
    DateTimeOffset? BackpressureResetAt,
    DateTimeOffset EvaluatedAt);
