namespace Harness.Persistence.Abstractions.WorkChain;

/// <summary>
/// Store durável do artefato de PLANO por demanda (PLAT-01). O plano gerado pelo planner puro vive
/// aqui, keyed por (tenant, demand), com status 'proposed' até ser materializado. Sobrevive a
/// reinício e é auditável. Gerar/ler é inerte; só a materialização cria work_tasks.
/// </summary>
public interface IDemandPlanStore
{
    /// <summary>
    /// Persiste o plano proposto. Idempotente por (tenant, demand): se já existe um plano para a
    /// demanda, devolve o existente sem sobrescrever. O booleano indica se um plano NOVO foi criado.
    /// </summary>
    Task<DemandPlanSaveResult> SaveProposedAsync(
        DemandPlanSaveCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lê o plano da demanda, ou nulo se não existir.</summary>
    Task<DemandPlanRecord?> GetByDemandAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default);

    /// <summary>Lê o plano por id, ou nulo se não existir.</summary>
    Task<DemandPlanRecord?> GetAsync(
        string tenantId, string planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Carimba o plano como materializado. Idempotente: só transiciona 'proposed' → 'materialized' e
    /// devolve true nessa primeira vez; devolve false se já estava materializado (não recria cards).
    /// </summary>
    Task<bool> TryMarkMaterializedAsync(
        string tenantId, string planId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Um card proposto persistido no plano (espelha o card puro do planner).</summary>
public sealed record DemandPlanCard(
    string ProposedTitle,
    string CardType,
    string RequiredRole,
    string Instruction,
    string InScope,
    string OutOfScope,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Gates,
    IReadOnlyList<string> Dependencies);

public sealed record DemandPlanSaveCommand(
    string TenantId,
    string Id,
    string ProjectId,
    string DemandId,
    string FeatureId,
    IReadOnlyList<DemandPlanCard> Cards,
    string CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record DemandPlanSaveResult(DemandPlanRecord Plan, bool Created);

public sealed record DemandPlanRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string DemandId,
    string FeatureId,
    string Status,
    IReadOnlyList<DemandPlanCard> Cards,
    string CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MaterializedAt);
