namespace Harness.Persistence.Abstractions.Delivery;

/// <summary>
/// DEL-03 — store durável e APPEND-ONLY das marcações tipadas capturadas durante a daily. Cada
/// marcação é uma NOVA linha: nunca há update nem delete, então o registro da daily é auditável e
/// nunca sofre sobrescrita silenciosa. NÃO cria nem toca cards de PO — é apenas a nota durável a
/// partir da qual o copiloto compõe o resumo pós-daily e a base do próximo briefing. Deriva a
/// identidade da entrega do projeto; não duplica dados de outras áreas.
/// </summary>
public interface IDeliveryDailyStore
{
    /// <summary>Anexa uma marcação tipada da daily. Retorna a linha persistida.</summary>
    Task<DeliveryDailyCaptureRecord> AppendAsync(
        DeliveryDailyCaptureAppendCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lista as marcações da entrega, da MAIS RECENTE para a mais antiga (limitado).</summary>
    Task<IReadOnlyList<DeliveryDailyCaptureRecord>> ListByProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default);
}

public sealed record DeliveryDailyCaptureAppendCommand(
    string TenantId,
    string Id,
    string ProjectId,
    string Kind,
    string Note,
    string CapturedBy,
    DateTimeOffset OccurredAt);

public sealed record DeliveryDailyCaptureRecord(
    string TenantId,
    string Id,
    string ProjectId,
    string Kind,
    string Note,
    string CapturedBy,
    DateTimeOffset CreatedAt);
