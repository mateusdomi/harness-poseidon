namespace Harness.Persistence.Abstractions.Coordination;

/// <summary>Um turno que a BRUNA disparou sozinha. Turno pedido pelo dono não entra aqui.</summary>
public sealed record ChiefSelfTriggeredTurnRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string DemandId,
    string? DemandPlanId,
    string? ChiefTurnId,
    string? CauseKey,
    DateTimeOffset OccurredAt);

/// <summary>Aresta causal materializada: <see cref="CauseKey"/> GEROU <see cref="EffectKey"/>.</summary>
public sealed record ChiefCausalEdgeRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string CauseKey,
    string EffectKey,
    string Relation,
    DateTimeOffset OccurredAt);

/// <summary>Interrupção auditada de uma guarda de laço, com a evidência que a justificou.</summary>
public sealed record ChiefLoopInterruptionRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string DemandId,
    string? DemandPlanId,
    string ReasonCode,
    string? Detail,
    IReadOnlyList<string> CyclePath,
    DateTimeOffset OccurredAt);

/// <summary>
/// Estado durável das guardas de laço da Bruna. Ver a migration 0098: em memória o laço reinicia
/// junto com o processo, que é exatamente quando ele volta a girar.
/// </summary>
public interface IChiefLoopGuardStore
{
    /// <summary>Registra um turno autodisparado. Idempotente por <paramref name="id"/>.</summary>
    Task<ChiefSelfTriggeredTurnRecord> RecordSelfTriggeredTurnAsync(
        ChiefSelfTriggeredTurnRecord record, CancellationToken cancellationToken = default);

    /// <summary>Acumulado de turnos autodisparados da demanda — alimenta o teto.</summary>
    Task<int> CountSelfTriggeredTurnsAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default);

    /// <summary>Turnos autodisparados da demanda desde um instante — alimenta a taxa por janela.</summary>
    Task<IReadOnlyList<DateTimeOffset>> ListSelfTriggeredTurnsSinceAsync(
        string tenantId, string demandId, DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializa "causa gerou efeito". A mesma tripla (causa, efeito, relação) é UM fato: sem
    /// isso, reprocessamento infla o grafo e o detector encontra caminhos que não existem.
    /// </summary>
    Task AddCausalEdgeAsync(
        ChiefCausalEdgeRecord record, CancellationToken cancellationToken = default);

    /// <summary>Arestas causais do projeto, para o detector de ciclo caminhar.</summary>
    Task<IReadOnlyList<ChiefCausalEdgeRecord>> ListCausalEdgesAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default);

    /// <summary>Registra a interrupção com a evidência — interromper sem dizer por quê é travar.</summary>
    Task RecordInterruptionAsync(
        ChiefLoopInterruptionRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChiefLoopInterruptionRecord>> ListInterruptionsAsync(
        string tenantId, string demandId, CancellationToken cancellationToken = default);
}
