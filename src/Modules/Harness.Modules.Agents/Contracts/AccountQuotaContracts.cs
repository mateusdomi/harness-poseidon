using System.Text.Json.Serialization;

namespace Harness.Modules.Agents.Contracts;

/// <summary>
/// Situação de cota de uma conta (N4/5.1). Conjunto fechado. <see cref="Unknown"/> é honesto
/// e é o padrão quando a CLI não expõe cota confiável — nunca se inventa um percentual.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuotaStatus>))]
public enum QuotaStatus
{
    /// <summary>Desconhecida: a CLI não publica cota confiável. Aplica-se limite local conservador.</summary>
    Unknown,

    /// <summary>Há margem observada na janela corrente.</summary>
    Available,

    /// <summary>Perto do limite observado; roteamento deve preferir alternativa.</summary>
    NearLimit,

    /// <summary>Esgotada na janela corrente; a conta não pode ser escalada até o reset.</summary>
    Exhausted,
}

/// <summary>Confiança na medição de cota. Fechado. Uma medição vencida cai de confiança.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuotaConfidence>))]
public enum QuotaConfidence
{
    Unknown,
    Low,
    Medium,
    High,
}

/// <summary>
/// Snapshot de cota para roteamento (N4/5.1). Traz a PROVENIÊNCIA da medição — fonte,
/// quando foi observada, quando vence (`StaleAfter`), quando a janela reseta e a confiança —
/// e um override auditado opcional. <see cref="RemainingFraction"/> é 0..1 e NULO quando
/// desconhecido: jamais um número inventado.
/// </summary>
public sealed record AccountQuotaSnapshot(
    string Source,
    DateTimeOffset ObservedAt,
    QuotaStatus Status,
    QuotaConfidence Confidence,
    double? RemainingFraction,
    DateTimeOffset? ResetAt,
    TimeSpan StaleAfter,
    string? OverrideReason = null)
{
    /// <summary>Cota desconhecida — a forma honesta quando a CLI não publica número confiável.</summary>
    public static AccountQuotaSnapshot UnknownFrom(
        string source, DateTimeOffset observedAt, TimeSpan staleAfter) =>
        new(source, observedAt, QuotaStatus.Unknown, QuotaConfidence.Unknown,
            RemainingFraction: null, ResetAt: null, StaleAfter: staleAfter);

    /// <summary>A medição venceu: não é mais confiável para bloquear/liberar por si só.</summary>
    public bool IsStale(DateTimeOffset now) => ObservedAt.Add(StaleAfter) < now;

    /// <summary>A janela ainda está esgotada neste instante (sem reset ou reset no futuro).</summary>
    public bool IsExhaustedAt(DateTimeOffset now) =>
        Status == QuotaStatus.Exhausted && (ResetAt is null || ResetAt.Value > now);
}
