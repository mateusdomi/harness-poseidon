namespace Harness.Persistence.Abstractions.WorkChain;

/// <summary>
/// Fase 0A1 (BR-004): registro DURÁVEL do compromisso de materializar o plano de uma demanda.
///
/// A linha nasce na MESMA transação factual que conclui o turno do Chefe e persiste suas demandas —
/// antes disso o turno não pode aparentar conclusão. Ela carrega a INTENÇÃO do turno (critérios de
/// aceite, especialidade e superfícies declaradas), que antes só existia na memória do worker e
/// desaparecia em qualquer queda, e o ESTADO factual do planejamento. Enquanto o registro não
/// estiver <c>completed</c>, o planejamento da demanda está declaradamente incompleto.
///
/// Este store NÃO é uma segunda fonte de verdade sobre cards: os cards continuam sendo o board e o
/// plano continua sendo <see cref="IDemandPlanStore"/>. Ele registra o progresso do trabalho de
/// materializar — o que o produto não sabia dizer antes.
/// </summary>
public interface IPlanMaterializationStore
{
    /// <summary>Lê o registro da demanda, ou nulo quando ela nunca pediu materialização.</summary>
    Task<PlanMaterializationRecord?> GetAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra o pedido de materialização. Idempotente por (tenant, demanda): um segundo pedido
    /// devolve o registro existente sem reabrir nem reescrever a intenção original.
    /// </summary>
    Task<PlanMaterializationRecord> RequestAsync(
        PlanMaterializationRequestCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adquire o trabalho: <c>pending</c>/<c>failed</c> (ou <c>processing</c> abandonado além de
    /// <see cref="PlanMaterializationBeginCommand.StaleAfter"/>) passam a <c>processing</c> com um
    /// novo dono e <c>attempt_count</c> incrementado — o par (dono, tentativa) é o fencing das
    /// escritas finais. Devolve nulo quando outro dono vivo detém o trabalho.
    /// </summary>
    Task<PlanMaterializationRecord?> TryBeginAsync(
        PlanMaterializationBeginCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conclui a materialização. Falso quando o dono perdeu o fencing — o trabalho é idempotente,
    /// então quem o tomou conclui no lugar; nada é corrompido e nada é declarado sem base.
    /// </summary>
    Task<bool> TryCompleteAsync(
        PlanMaterializationCompleteCommand command, CancellationToken cancellationToken = default);

    /// <summary>Registra a falha da tentativa com o código do erro. Mesmo fencing do complete.</summary>
    Task<bool> TryFailAsync(
        PlanMaterializationFailCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Página de registros para o reconciliador, em ordem estável de (tenant, demanda), a partir do
    /// cursor exclusivo. Devolve TODOS os estados: o reconciliador precisa reabrir tanto o trabalho
    /// abandonado quanto o <c>completed</c> cuja realidade no board divergiu.
    /// </summary>
    Task<IReadOnlyList<PlanMaterializationRecord>> ListForReconciliationAsync(
        PlanMaterializationCursor? after, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Cursor exclusivo e estável da varredura de reconciliação.</summary>
public sealed record PlanMaterializationCursor(string TenantId, string DemandId);

public static class PlanMaterializationStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsKnown(string value) =>
        value is Pending or Processing or Completed or Failed;
}

/// <summary>
/// A intenção declarada pelo turno para o planejamento da demanda. Sem ela, um replanejamento após
/// reinício reconstruiria o plano com hipóteses diferentes das do turno original.
/// </summary>
public sealed record PlanMaterializationRequest(
    IReadOnlyList<string> AcceptanceCriteria,
    string? Specialty = null,
    PlanMaterializationSurfaces? Surfaces = null);

/// <summary>Superfícies declaradas pelo Chefe; cada campo é tri-state (nulo = não declarei).</summary>
public sealed record PlanMaterializationSurfaces(
    bool? Frontend = null,
    bool? Backend = null,
    bool? ExternalCredential = null,
    bool? TechnicalUncertainty = null,
    bool? Decision = null,

    /// <summary>
    /// Falta decidir a TECNOLOGIA CONCRETA (stack). Diferente de <see cref="Decision"/>: aquela é
    /// decisão de negócio e pertence ao humano; esta é card do Arquiteto e é despachável.
    /// </summary>
    bool? ArchitectureDecision = null);

public sealed record PlanMaterializationRecord(
    string TenantId,
    string DemandId,
    string ProjectId,
    string? TurnId,
    string? PlanId,
    string Status,
    int AttemptCount,
    int? ExpectedCards,
    int? MaterializedCards,
    PlanMaterializationRequest Request,
    string? OwnerId,
    string? LastError,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record PlanMaterializationRequestCommand(
    string TenantId,
    string DemandId,
    string ProjectId,
    string? TurnId,
    PlanMaterializationRequest Request,
    DateTimeOffset OccurredAt);

public sealed record PlanMaterializationBeginCommand(
    string TenantId,
    string DemandId,
    string OwnerId,
    DateTimeOffset Now,
    TimeSpan StaleAfter,
    /// <summary>
    /// Reabre um registro já <c>completed</c>. Exclusivo do reconciliador, e só depois de ele
    /// COMPROVAR divergência entre o registro e o board — nunca por suspeita.
    /// </summary>
    bool AllowCompleted = false);

public sealed record PlanMaterializationCompleteCommand(
    string TenantId,
    string DemandId,
    string OwnerId,
    int AttemptCount,
    string PlanId,
    int ExpectedCards,
    int MaterializedCards,
    DateTimeOffset OccurredAt);

public sealed record PlanMaterializationFailCommand(
    string TenantId,
    string DemandId,
    string OwnerId,
    int AttemptCount,
    string ErrorCode,
    DateTimeOffset OccurredAt);
