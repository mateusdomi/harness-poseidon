namespace Harness.Modules.Governance.Metrics;

/// <summary>
/// PLAT-04: agrega, de forma PURA e determinística, os resultados de tentativas por feature.
///
/// Cada número (contagens, custo, tokens, latência) deriva estritamente das tentativas gravadas —
/// não há valores inventados. A classificação de desfecho por tentativa é mutuamente exclusiva:
/// sucesso XOR falha XOR em-andamento, com precedência aprovado &gt; rejeitado/falhou/cancelado.
/// </summary>
public static class FeatureMetricsAggregator
{
    public static FeatureMetricsSnapshot Aggregate(
        string projectId, IReadOnlyCollection<FeatureAttemptInput> attempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(attempts);

        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            ArgumentNullException.ThrowIfNull(attempt);
            var featureId = FeatureIdParser.Parse(attempt.TaskTitle);
            if (!buckets.TryGetValue(featureId, out var bucket))
            {
                bucket = new Bucket();
                buckets[featureId] = bucket;
            }

            bucket.Add(attempt);
        }

        var features = buckets
            .Select(pair => pair.Value.ToMetric(pair.Key))
            .OrderBy(metric => metric.FeatureId, StringComparer.Ordinal)
            .ToArray();
        return new FeatureMetricsSnapshot(projectId, features);
    }

    public static AttemptOutcome Classify(string state, string operationalState)
    {
        if (string.Equals(state, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return AttemptOutcome.Succeeded;
        }

        if (string.Equals(state, "rejected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operationalState, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operationalState, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return AttemptOutcome.Failed;
        }

        return AttemptOutcome.InProgress;
    }

    private sealed class Bucket
    {
        private readonly HashSet<string> _tasks = new(StringComparer.Ordinal);
        private int _attempts;
        private int _success;
        private int _failure;
        private int _inProgress;
        private decimal _cost;
        private long _tokensInput;
        private long _tokensOutput;
        private long _durationMs;

        public void Add(FeatureAttemptInput attempt)
        {
            _tasks.Add(attempt.TaskId);
            _attempts++;
            switch (Classify(attempt.State, attempt.OperationalState))
            {
                case AttemptOutcome.Succeeded: _success++; break;
                case AttemptOutcome.Failed: _failure++; break;
                default: _inProgress++; break;
            }

            _cost += attempt.CostUsd;
            _tokensInput += attempt.TokensInput;
            _tokensOutput += attempt.TokensOutput;
            _durationMs += attempt.DurationMs ?? 0;
        }

        public FeatureMetric ToMetric(string featureId) => new(
            featureId, _tasks.Count, _attempts, _success, _failure, _inProgress,
            _cost, _tokensInput, _tokensOutput, _durationMs);
    }
}

public enum AttemptOutcome
{
    InProgress,
    Succeeded,
    Failed,
}

public sealed record FeatureAttemptInput(
    string TaskId, string TaskTitle, string State, string OperationalState,
    decimal CostUsd, long TokensInput, long TokensOutput, long? DurationMs);

public sealed record FeatureMetric(
    string FeatureId, int TaskCount, int AttemptCount, int SuccessCount, int FailureCount,
    int InProgressCount, decimal TotalCostUsd, long TotalTokensInput, long TotalTokensOutput,
    long TotalDurationMs);

public sealed record FeatureMetricsSnapshot(
    string ProjectId, IReadOnlyList<FeatureMetric> Features);
