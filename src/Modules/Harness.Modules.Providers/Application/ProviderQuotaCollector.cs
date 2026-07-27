using Harness.Modules.Providers.Contracts;
using Harness.SharedKernel.Providers;

namespace Harness.Modules.Providers.Application;

/// <summary>
/// Coletor de cotas por provedor sobre <see cref="QuotaStatusRecord"/> (Fase 3).
/// Transforma medições reais de uso e invocação em snapshots de cota auditáveis.
/// Semântica honesta: quando a CLI não expõe cota confiável, o status permanece "Unknown"
/// com fração nula — jamais um número inventado.
/// </summary>
public sealed class ProviderQuotaCollector
{
    private readonly TimeSpan _defaultStaleAfter;
    private readonly double _nearLimitThreshold;

    public ProviderQuotaCollector(
        TimeSpan? defaultStaleAfter = null,
        double nearLimitThreshold = 0.15)
    {
        _defaultStaleAfter = defaultStaleAfter ?? TimeSpan.FromMinutes(15);
        _nearLimitThreshold = nearLimitThreshold;
    }

    /// <summary>
    /// Calcula o snapshot de cota de uma conta com base no histórico recente de invocações e overrides.
    /// </summary>
    public QuotaStatusRecord Collect(
        string accountAlias,
        string provider,
        IReadOnlyList<ModelInvocationRecord> recentInvocations,
        DateTimeOffset now,
        QuotaStatusRecord? existingSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(accountAlias);
        ArgumentNullException.ThrowIfNull(provider);

        // Se houver override ativo não expirado no snapshot existente, ele prevalece.
        if (existingSnapshot is not null &&
            !string.IsNullOrWhiteSpace(existingSnapshot.OverrideReason) &&
            existingSnapshot.ObservedAt.Add(existingSnapshot.StaleAfter) >= now)
        {
            return existingSnapshot;
        }

        // Se não houver medição de cota confiável registrada, retorna Unknown honesto.
        if (existingSnapshot is null || string.Equals(existingSnapshot.Status, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            var quotaError = recentInvocations.FirstOrDefault(inv =>
                string.Equals(inv.AccountAlias, accountAlias, StringComparison.OrdinalIgnoreCase) &&
                IsQuotaExceeded(inv.Outcome) &&
                inv.InvokedAt >= now.AddMinutes(-30));

            if (quotaError is not null)
            {
                return new QuotaStatusRecord(
                    Source: $"collector:{provider}",
                    ObservedAt: quotaError.InvokedAt,
                    Status: "Exhausted",
                    Confidence: "High",
                    RemainingFraction: 0.0,
                    ResetAt: quotaError.InvokedAt.AddMinutes(30),
                    StaleAfter: _defaultStaleAfter,
                    OverrideReason: "inferred_from_quota_exceeded_outcome");
            }

            return new QuotaStatusRecord(
                Source: $"collector:{provider}",
                ObservedAt: now,
                Status: "Unknown",
                Confidence: "Unknown",
                RemainingFraction: null,
                ResetAt: null,
                StaleAfter: _defaultStaleAfter);
        }

        // Se o snapshot existente venceu (stale), rebaixa a confiança.
        if (existingSnapshot.ObservedAt.Add(existingSnapshot.StaleAfter) < now)
        {
            return existingSnapshot with
            {
                Confidence = "Low",
                StaleAfter = _defaultStaleAfter
            };
        }

        // Avalia o limite em relação à fração restante observada.
        if (existingSnapshot.RemainingFraction.HasValue)
        {
            var remaining = existingSnapshot.RemainingFraction.Value;
            var newStatus = remaining <= 0.0
                ? "Exhausted"
                : remaining <= _nearLimitThreshold
                    ? "NearLimit"
                    : "Available";

            return existingSnapshot with
            {
                Status = newStatus,
                ObservedAt = now
            };
        }

        return existingSnapshot;
    }

    private static bool IsQuotaExceeded(string outcome)
    {
        var normalized = outcome.Split('|', 2, StringSplitOptions.TrimEntries)[0]
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return string.Equals(normalized, "quotaexceeded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "quotaexhausted", StringComparison.OrdinalIgnoreCase);
    }
}
