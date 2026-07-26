using System.Collections.Concurrent;
using Harness.Modules.Providers.Contracts;

namespace Harness.Modules.Providers.Application;

public sealed record SimpleAccountSpec(
    string Alias,
    string ProviderKind,
    IReadOnlyList<string> AllowedRoles,
    IReadOnlyList<string> AllowedPathScopes,
    int ConcurrencyLimit,
    int ActiveAttempts,
    int Priority);

/// <summary>
/// Gerenciador de capacidade da frota de provedores (Fase 3 / N4).
/// Controla snapshots de cota, circuit breaker de falhas consecutivas e backpressure determinístico.
/// </summary>
public sealed class CapacityManager
{
    private readonly ConcurrentDictionary<string, QuotaStatusRecord> _quotas =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, int> _consecutiveFailures =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _circuitTripTimes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly int _consecutiveFailureThreshold;
    private readonly TimeSpan _circuitCooldownDuration;

    public CapacityManager(
        int consecutiveFailureThreshold = 3,
        TimeSpan? circuitCooldownDuration = null)
    {
        _consecutiveFailureThreshold = consecutiveFailureThreshold;
        _circuitCooldownDuration = circuitCooldownDuration ?? TimeSpan.FromMinutes(5);
    }

    public void UpdateQuotaSnapshot(string accountAlias, QuotaStatusRecord snapshot)
    {
        ArgumentNullException.ThrowIfNull(accountAlias);
        ArgumentNullException.ThrowIfNull(snapshot);

        _quotas[accountAlias] = snapshot;
    }

    public QuotaStatusRecord GetQuotaSnapshot(string accountAlias, DateTimeOffset now)
    {
        if (_quotas.TryGetValue(accountAlias, out var snapshot))
        {
            return snapshot;
        }

        return new QuotaStatusRecord("capacity_manager", now, "Unknown", "Unknown", null, null, TimeSpan.FromMinutes(15));
    }

    public IReadOnlyDictionary<string, QuotaStatusRecord> GetAllQuotas(DateTimeOffset now)
    {
        var result = new Dictionary<string, QuotaStatusRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, snapshot) in _quotas)
        {
            result[alias] = snapshot;
        }
        return result;
    }

    public void RecordInvocationOutcome(
        string accountAlias,
        string provider,
        string outcome,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accountAlias);
        ArgumentNullException.ThrowIfNull(outcome);

        if (string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase))
        {
            _consecutiveFailures[accountAlias] = 0;
            _circuitTripTimes.TryRemove(accountAlias, out _);
            return;
        }

        var failures = _consecutiveFailures.AddOrUpdate(
            accountAlias, 1, (_, current) => current + 1);

        if (failures >= _consecutiveFailureThreshold)
        {
            _circuitTripTimes[accountAlias] = now;
            var currentQuota = GetQuotaSnapshot(accountAlias, now);
            var updated = currentQuota with
            {
                Status = "Exhausted",
                ResetAt = now.Add(_circuitCooldownDuration),
                OverrideReason = $"circuit_breaker_tripped_after_{failures}_failures"
            };
            _quotas[accountAlias] = updated;
        }
    }

    public bool IsCircuitTripped(string accountAlias, DateTimeOffset now)
    {
        if (_circuitTripTimes.TryGetValue(accountAlias, out var tripTime))
        {
            if (now < tripTime.Add(_circuitCooldownDuration))
            {
                return true;
            }

            _circuitTripTimes.TryRemove(accountAlias, out _);
            _consecutiveFailures[accountAlias] = 0;
        }

        return false;
    }

    public CapacityStatusSnapshot EvaluateCapacity(
        IEnumerable<SimpleAccountSpec> availableAccounts,
        string role,
        DateTimeOffset now)
    {
        var quotas = GetAllQuotas(now);
        var failures = new Dictionary<string, int>(_consecutiveFailures, StringComparer.OrdinalIgnoreCase);

        var matchingAccounts = availableAccounts
            .Where(acc => acc.AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matchingAccounts.Count == 0)
        {
            return new CapacityStatusSnapshot(
                quotas, failures, IsUnderBackpressure: true, BackpressureResetAt: null, EvaluatedAt: now);
        }

        var allExhausted = true;
        DateTimeOffset? minResetAt = null;

        foreach (var account in matchingAccounts)
        {
            var isExhausted = false;
            if (quotas.TryGetValue(account.Alias, out var q))
            {
                if (string.Equals(q.Status, "Exhausted", StringComparison.OrdinalIgnoreCase) &&
                    (q.ResetAt is null || q.ResetAt.Value > now))
                {
                    isExhausted = true;
                    if (q.ResetAt.HasValue && (minResetAt is null || q.ResetAt.Value < minResetAt.Value))
                    {
                        minResetAt = q.ResetAt.Value;
                    }
                }
            }

            if (IsCircuitTripped(account.Alias, now))
            {
                isExhausted = true;
            }

            if (!isExhausted)
            {
                allExhausted = false;
                break;
            }
        }

        return new CapacityStatusSnapshot(
            quotas, failures, IsUnderBackpressure: allExhausted, BackpressureResetAt: allExhausted ? minResetAt : null, EvaluatedAt: now);
    }
}
