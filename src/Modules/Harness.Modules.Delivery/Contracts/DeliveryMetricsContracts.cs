namespace Harness.Modules.Delivery.Contracts;

// DEL-06 — Métricas. Métricas DORA (por aplicação/entrega) e métricas próprias da entrega. TODAS
// derivam ESTRITAMENTE de dados gravados (tentativas, planos, previsões, relatórios); NADA é inventado.
// Quando um insumo de uma métrica NÃO está registrado, ela é exposta como "não medida" (Measured=false,
// Value=null) com uma base honesta — em vez de um número fabricado.

/// <summary>
/// Uma métrica derivada. <see cref="Measured"/> falso significa que o insumo não está registrado:
/// <see cref="Value"/> é nulo e <see cref="Basis"/> explica o porquê. Quando medida, <see cref="Value"/>
/// é o valor derivado e <see cref="Unit"/> a sua unidade. Determinística: mesmos fatos ⇒ mesma saída.
/// </summary>
public sealed record DeliveryMetricContract(
    string Key,
    string Label,
    string Category,
    bool Measured,
    string? Value,
    string? Unit,
    string Basis);

public sealed record DeliveryMetricsContract(
    string DeliveryId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DeliveryMetricContract> Dora,
    IReadOnlyList<DeliveryMetricContract> Own);
