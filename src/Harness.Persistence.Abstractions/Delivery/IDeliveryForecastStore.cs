namespace Harness.Persistence.Abstractions.Delivery;

/// <summary>
/// DEL-09 — store durável e APPEND-ONLY do histórico de previsões honestas por entrega (projeto).
/// Cada previsão é uma NOVA linha: nunca há update nem delete, então o "histórico de previsão" é
/// auditável e nunca sofre sobrescrita silenciosa (DEL-02, Plano&amp;marcos). Deriva a identidade da
/// entrega do projeto — não duplica dados de outras áreas.
/// </summary>
public interface IDeliveryForecastStore
{
    /// <summary>Anexa uma nova previsão ao histórico da entrega. Retorna a linha persistida.</summary>
    Task<DeliveryForecastRecord> AppendAsync(
        DeliveryForecastAppendCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lista o histórico da entrega, da MAIS RECENTE para a mais antiga (limitado).</summary>
    Task<IReadOnlyList<DeliveryForecastRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default);

    /// <summary>A previsão mais recente da entrega, ou nulo se ainda não houver histórico.</summary>
    Task<DeliveryForecastRecord?> GetLatestAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default);
}

public sealed record DeliveryForecastBasisEntry(string Signal, string Detail);

public sealed record DeliveryForecastAppendCommand(
    string TenantId,
    string Id,
    string ProjectId,
    DateTimeOffset? ForecastDate,
    string Confidence,
    int ConfidencePercent,
    bool HasSufficientEvidence,
    IReadOnlyList<DeliveryForecastBasisEntry> Basis,
    DateTimeOffset OccurredAt);

public sealed record DeliveryForecastRecord(
    string TenantId,
    string Id,
    string ProjectId,
    DateTimeOffset? ForecastDate,
    string Confidence,
    int ConfidencePercent,
    bool HasSufficientEvidence,
    IReadOnlyList<DeliveryForecastBasisEntry> Basis,
    DateTimeOffset CreatedAt);
