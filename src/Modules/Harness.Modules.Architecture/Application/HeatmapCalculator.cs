using Harness.Modules.Architecture.Contracts;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-02 — derivação dos sinais de heatmap de um sistema. PURA e determinística. Cada sinal deriva
/// ESTRITAMENTE de um metadado gravado; nenhum é inventado. O score é a contagem de sinais ativos —
/// grosseiro por design, comunica "quão quente" o sistema está para atenção do arquiteto.
/// </summary>
public static class HeatmapCalculator
{
    /// <summary>Limiar transparente de custo mensal (USD) a partir do qual o sinal de custo acende.</summary>
    public const decimal HighCostThresholdUsd = 10_000m;

    public static readonly IReadOnlyList<string> SignalCodes =
    [
        "tech-obsolete", "no-owner", "no-doc", "critical",
        "cost", "incidents", "duplicity", "risk", "bus-factor",
    ];

    private static readonly HashSet<string> Obsolete =
        new(StringComparer.OrdinalIgnoreCase) { "obsolete", "deprecated", "eol", "end-of-life" };

    public static IReadOnlyList<HeatSignalContract> Signals(ArchSystemMetadata system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var signals = new List<HeatSignalContract>();

        if (system.LifecycleStatus is not null && Obsolete.Contains(system.LifecycleStatus))
        {
            signals.Add(new HeatSignalContract("tech-obsolete", $"lifecycle status is '{system.LifecycleStatus}'"));
        }

        if (string.IsNullOrWhiteSpace(system.Owner))
        {
            signals.Add(new HeatSignalContract("no-owner", "no owner recorded"));
        }

        if (system.Documents.Count == 0)
        {
            signals.Add(new HeatSignalContract("no-doc", "no documentation linked"));
        }

        if (string.Equals(system.Criticality, "critical", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add(new HeatSignalContract("critical", "criticality is 'critical'"));
        }

        if (system.CostMonthlyUsd is { } cost && cost >= HighCostThresholdUsd)
        {
            signals.Add(new HeatSignalContract("cost", $"monthly cost {cost:0} USD ≥ {HighCostThresholdUsd:0}"));
        }

        if (system.IncidentCount > 0)
        {
            signals.Add(new HeatSignalContract("incidents", $"{system.IncidentCount} incident(s) recorded"));
        }

        if (!string.IsNullOrWhiteSpace(system.DuplicateOfId))
        {
            signals.Add(new HeatSignalContract("duplicity", $"marked duplicate of {system.DuplicateOfId}"));
        }

        if (string.Equals(system.RiskLevel, "high", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add(new HeatSignalContract("risk", "risk level is 'high'"));
        }

        if (system.BusFactor is { } factor && factor <= 1)
        {
            signals.Add(new HeatSignalContract("bus-factor", $"bus factor {factor}"));
        }

        return signals;
    }

    public static int Score(ArchSystemMetadata system) => Signals(system).Count;
}
