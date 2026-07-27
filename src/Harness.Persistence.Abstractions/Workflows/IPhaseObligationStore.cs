namespace Harness.Persistence.Abstractions.Workflows;

/// <summary>
/// Store durável do PLANO DE FASE materializado: as obrigações reais que a fase precisa cumprir
/// para estar tecnicamente pronta. É a fonte do denominador do progresso.
///
/// O plano é VERSIONADO. Uma obrigação legítima que surge no meio da fase não pode reescrever o
/// passado nem mover o indicador em silêncio: ela entra numa versão nova, com motivo registrado, e
/// a versão anterior fica preservada para reconstrução.
/// </summary>
public interface IPhaseObligationStore
{
    /// <summary>
    /// Grava o plano da fase de forma idempotente por (run, fase, versão, chave). Uma obrigação
    /// que já existe naquela versão NÃO é sobrescrita — o estado dela é do runtime, não do plano.
    /// Devolve quantas obrigações foram efetivamente criadas nesta chamada.
    /// </summary>
    Task<int> EnsurePlanAsync(
        PhaseObligationPlanCommand command, CancellationToken cancellationToken = default);

    /// <summary>Obrigações da versão vigente (a maior) da fase.</summary>
    Task<IReadOnlyList<PhaseObligationRecord>> ListCurrentAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default);

    /// <summary>Todas as versões, para auditoria e reconstrução do histórico.</summary>
    Task<IReadOnlyList<PhaseObligationRecord>> ListAllVersionsAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default);

    /// <summary>A maior versão de plano já gravada para a fase; 0 quando não há plano.</summary>
    Task<int> CurrentPlanVersionAsync(
        string tenantId, string runId, string phaseKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atualiza o estado (e as evidências) de uma obrigação. Cancelar EXIGE motivo: sem ele a
    /// mutação é recusada, porque remover a obrigação que falhou é o caminho curto para fabricar
    /// 100%.
    /// </summary>
    Task<bool> UpdateStateAsync(
        PhaseObligationStateCommand command, CancellationToken cancellationToken = default);
}

public sealed record PhaseObligationInput(
    string ObligationKey,
    string Kind,
    string Description,
    bool Required,
    double Weight,
    string Source,
    string? CompletionCriteria = null,
    string? CardId = null,
    string? ObjectiveKey = null,
    string? ArtifactRef = null,
    string? Reason = null);

public sealed record PhaseObligationPlanCommand(
    string TenantId,
    string ProjectId,
    string RunId,
    string PhaseKey,
    int PlanVersion,
    IReadOnlyList<PhaseObligationInput> Obligations,
    DateTimeOffset OccurredAt);

public sealed record PhaseObligationStateCommand(
    string TenantId,
    string ObligationId,
    string State,
    IReadOnlyList<string> Evidence,
    string? Reason,
    DateTimeOffset OccurredAt);

public sealed record PhaseObligationRecord(
    string TenantId,
    string ObligationId,
    string ProjectId,
    string RunId,
    string PhaseKey,
    string ObligationKey,
    int PlanVersion,
    string Kind,
    string Description,
    bool Required,
    double Weight,
    string Source,
    string? CompletionCriteria,
    string? CardId,
    string? ObjectiveKey,
    string? ArtifactRef,
    string State,
    IReadOnlyList<string> Evidence,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Cancelamento sem justificativa: recusado por design, não por validação de forma.</summary>
public sealed class PhaseObligationValidationException(string message) : Exception(message);
