using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-07 — Insights &amp; Racionalização. Análise PURA e determinística sobre o modelo VIGENTE: detecta
/// sobreposição funcional/unificação, tech fora de suporte, integrações ponto-a-ponto, acesso direto a
/// banco alheio, SPOF e custo×uso. Cada insight é uma PROPOSTA (classificação + racional + evidência +
/// impacto), NUNCA uma ação: o agente sugere, o humano decide. Tudo deriva estritamente de fatos gravados.
/// </summary>
public static class RationalizationAnalyzer
{
    /// <summary>Limiar transparente de grau de integração a partir do qual acende o sinal ponto-a-ponto.</summary>
    public const int PointToPointDegreeThreshold = 4;

    /// <summary>Nº de dependentes a partir do qual um sistema critical vira candidato a SPOF.</summary>
    public const int SpofDependentThreshold = 3;

    private static readonly HashSet<string> Obsolete =
        new(StringComparer.OrdinalIgnoreCase) { "obsolete", "deprecated", "eol", "end-of-life" };

    public static RationalizationReportContract Analyze(ArchitectureModelInput model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var systems = SystemsMapProjector.Systems(model);
        var metadataById = model.Systems.ToDictionary(s => s.ElementId, StringComparer.Ordinal);
        var systemIds = systems.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // Índice de contenção: qual sistema CONTÉM cada elemento (para acesso direto a banco alheio).
        var containerOwner = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented && r.Kind == "contains" && systemIds.Contains(r.SourceId))
            .GroupBy(r => r.TargetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SourceId, StringComparer.Ordinal);

        // Grau de integração ponto-a-ponto: arestas de dependência sistema→sistema.
        var outDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rel in model.Relationships)
        {
            if (rel.State == ArchitectureKinds.Implemented &&
                ArchitectureKinds.IsDependencyEdge(rel.Kind) &&
                systemIds.Contains(rel.SourceId) && systemIds.Contains(rel.TargetId))
            {
                outDegree[rel.SourceId] = outDegree.GetValueOrDefault(rel.SourceId) + 1;
            }
        }

        var insights = new List<RationalizationInsightContract>();
        foreach (var system in systems)
        {
            var meta = metadataById.GetValueOrDefault(system.Id);
            var dependents = DependencyAnalyzer.WhoDependsOn(model, system.Id);
            var dependentCount = dependents.DirectCount + dependents.TransitiveCount;
            var before = insights.Count;

            // Sobreposição funcional / unificação: outro sistema com a mesma capacidade no mesmo domínio.
            foreach (var overlap in FindOverlaps(system.Id, meta, systems, metadataById))
            {
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "functional-overlap", "consolidate",
                    $"Shares capability '{overlap.Capability}' with '{overlap.OtherName}' in domain '{overlap.Domain}'.",
                    $"capability '{overlap.Capability}' recorded on both systems in domain '{overlap.Domain}'",
                    dependentCount, [overlap.OtherId]));
            }

            // Duplicidade declarada -> consolidar.
            if (meta is { DuplicateOfId: { Length: > 0 } duplicateOf })
            {
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "unification", "consolidate",
                    $"Marked as duplicate of {duplicateOf}.",
                    $"duplicateOfId={duplicateOf} recorded", dependentCount, [duplicateOf]));
            }

            // Tech fora de suporte -> modernizar; se também critical, substituir.
            if (meta?.LifecycleStatus is { } lifecycle && Obsolete.Contains(lifecycle))
            {
                var isCritical = string.Equals(meta.Criticality, "critical", StringComparison.OrdinalIgnoreCase);
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "unsupported-tech", isCritical ? "replace" : "modernize",
                    $"Lifecycle status is '{lifecycle}'{(isCritical ? " on a critical system" : string.Empty)}.",
                    $"lifecycleStatus='{lifecycle}' recorded", dependentCount, []));
            }

            // Integrações ponto-a-ponto -> investigar hub de integração.
            if (outDegree.GetValueOrDefault(system.Id) >= PointToPointDegreeThreshold)
            {
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "point-to-point", "investigate",
                    $"Integrates point-to-point with {outDegree[system.Id]} systems.",
                    $"{outDegree[system.Id]} direct system→system dependency edges recorded", dependentCount, []));
            }

            // Acesso direto a banco alheio -> investigar/substituir integração.
            foreach (var foreignOwner in ForeignDataAccess(system.Id, model, containerOwner, systemIds))
            {
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "direct-db-access", "investigate",
                    $"Accesses a dataStore owned by another system ({foreignOwner}).",
                    $"dependency edge to a dataStore contained by '{foreignOwner}'", dependentCount, [foreignOwner]));
            }

            // SPOF: sistema critical com muitos dependentes.
            if (meta is not null &&
                string.Equals(meta.Criticality, "critical", StringComparison.OrdinalIgnoreCase) &&
                dependentCount >= SpofDependentThreshold)
            {
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "spof", "investigate",
                    $"Critical system with {dependentCount} dependents — single point of failure risk.",
                    $"criticality='critical' and {dependentCount} dependents recorded", dependentCount, []));
            }

            // Custo × uso: alto custo e ZERO dependentes -> candidato a desativação.
            if (meta?.CostMonthlyUsd is { } cost && cost >= HeatmapCalculator.HighCostThresholdUsd && dependentCount == 0)
            {
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "cost-vs-use", "decommission",
                    $"Monthly cost {cost:0} USD with no recorded dependents.",
                    $"costMonthlyUsd={cost:0} and 0 dependents recorded", 0, []));
            }

            // Sem nenhum sinal -> manter.
            if (insights.Count == before)
            {
                insights.Add(new RationalizationInsightContract(
                    system.Id, system.Name, "cost-vs-use", "keep",
                    "No rationalization signal detected.", "no adverse fact recorded", dependentCount, []));
            }
        }

        var ordered = insights
            .OrderBy(i => i.SystemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Category, StringComparer.Ordinal)
            .ToArray();

        var byClassification = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var insight in ordered)
        {
            byClassification[insight.Classification] = byClassification.GetValueOrDefault(insight.Classification) + 1;
        }

        return new RationalizationReportContract(systems.Count, ordered.Length, byClassification, ordered);
    }

    private static IEnumerable<(string Capability, string Domain, string OtherId, string OtherName)> FindOverlaps(
        string systemId, ArchSystemMetadata? meta, IReadOnlyList<ArchElement> systems,
        Dictionary<string, ArchSystemMetadata> metadataById)
    {
        if (meta?.Domain is not { Length: > 0 } domain || meta.Capabilities.Count == 0)
        {
            yield break;
        }

        foreach (var other in systems)
        {
            if (string.Equals(other.Id, systemId, StringComparison.Ordinal))
            {
                continue;
            }

            var otherMeta = metadataById.GetValueOrDefault(other.Id);
            if (otherMeta?.Domain is not { } otherDomain ||
                !string.Equals(otherDomain, domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var shared = meta.Capabilities
                .Intersect(otherMeta.Capabilities, StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            // Só reporta uma vez por par ordenado (o de id menor reporta) para evitar duplicidade.
            if (shared is not null && string.CompareOrdinal(systemId, other.Id) < 0)
            {
                yield return (shared, domain, other.Id, other.Name);
            }
        }
    }

    private static IEnumerable<string> ForeignDataAccess(
        string systemId, ArchitectureModelInput model, Dictionary<string, string> containerOwner,
        HashSet<string> systemIds)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in model.Relationships)
        {
            if (rel.State != ArchitectureKinds.Implemented ||
                !ArchitectureKinds.IsDependencyEdge(rel.Kind) ||
                !string.Equals(rel.SourceId, systemId, StringComparison.Ordinal))
            {
                continue;
            }

            if (containerOwner.TryGetValue(rel.TargetId, out var owner) &&
                !string.Equals(owner, systemId, StringComparison.Ordinal) &&
                systemIds.Contains(owner) &&
                reported.Add(owner))
            {
                yield return owner;
            }
        }
    }
}
