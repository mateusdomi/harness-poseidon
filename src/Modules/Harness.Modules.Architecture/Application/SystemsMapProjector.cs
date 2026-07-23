using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-02 — Mapa Corporativo de Sistemas. Projeta PURA e deterministicamente o catálogo de sistemas
/// (elementos kind=system), o mapa por domínio, o mapa por capacidade, o grafo de integração
/// (relacionamentos entre sistemas) e o heatmap. Tudo deriva estritamente dos fatos gravados.
/// </summary>
public static class SystemsMapProjector
{
    public static SystemCatalogEntryContract CatalogEntry(ArchElement element, ArchSystemMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(element);
        var meta = metadata ?? Empty(element.Id);
        var signals = HeatmapCalculator.Signals(meta);
        return new SystemCatalogEntryContract(
            element.Id, element.Name, element.Description, meta.Domain, meta.Capabilities,
            meta.Owner, meta.Criticality, meta.LifecycleStatus,
            signals.Select(s => s.Code).ToArray(), signals.Count);
    }

    public static DomainMapContract DomainMap(ArchitectureModelInput model)
    {
        var systems = Systems(model);
        var byMetadata = MetadataById(model);
        var groups = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in systems)
        {
            var domain = byMetadata.GetValueOrDefault(system.Id)?.Domain;
            var key = string.IsNullOrWhiteSpace(domain) ? "(unassigned)" : domain!;
            if (!groups.TryGetValue(key, out var ids))
            {
                ids = [];
                groups[key] = ids;
            }

            ids.Add(system.Id);
        }

        var nodes = groups
            .Select(g => new DomainMapNodeContract(g.Key, g.Value.Count, g.Value.OrderBy(x => x, StringComparer.Ordinal).ToArray()))
            .ToArray();
        return new DomainMapContract(nodes.Length, nodes);
    }

    public static CapabilityMapContract CapabilityMap(ArchitectureModelInput model)
    {
        var systems = Systems(model);
        var byMetadata = MetadataById(model);
        var groups = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in systems)
        {
            var capabilities = byMetadata.GetValueOrDefault(system.Id)?.Capabilities ?? [];
            var effective = capabilities.Count == 0 ? new[] { "(uncapability)" } : capabilities.ToArray();
            foreach (var capability in effective)
            {
                var key = string.IsNullOrWhiteSpace(capability) ? "(uncapability)" : capability;
                if (!groups.TryGetValue(key, out var ids))
                {
                    ids = [];
                    groups[key] = ids;
                }

                ids.Add(system.Id);
            }
        }

        var nodes = groups
            .Select(g => new CapabilityMapNodeContract(
                g.Key, g.Value.Count, g.Value.OrderBy(x => x, StringComparer.Ordinal).ToArray()))
            .ToArray();
        return new CapabilityMapContract(nodes.Length, nodes);
    }

    public static IntegrationGraphContract IntegrationGraph(ArchitectureModelInput model)
    {
        var systems = Systems(model).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var edges = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented &&
                systems.ContainsKey(r.SourceId) && systems.ContainsKey(r.TargetId))
            .OrderBy(r => systems[r.SourceId].Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => systems[r.TargetId].Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new IntegrationEdgeContract(
                r.SourceId, systems[r.SourceId].Name, r.TargetId, systems[r.TargetId].Name, r.Kind))
            .ToArray();
        return new IntegrationGraphContract(systems.Count, edges.Length, edges);
    }

    public static SystemHeatmapContract Heatmap(ArchitectureModelInput model)
    {
        var systems = Systems(model);
        var byMetadata = MetadataById(model);
        var totals = HeatmapCalculator.SignalCodes.ToDictionary(c => c, _ => 0, StringComparer.Ordinal);
        var entries = new List<SystemHeatmapEntryContract>();
        foreach (var system in systems)
        {
            var meta = byMetadata.GetValueOrDefault(system.Id) ?? Empty(system.Id);
            var signals = HeatmapCalculator.Signals(meta);
            foreach (var signal in signals)
            {
                totals[signal.Code] = totals.GetValueOrDefault(signal.Code) + 1;
            }

            entries.Add(new SystemHeatmapEntryContract(
                system.Id, system.Name, meta.Domain, signals.Count, signals));
        }

        var ordered = entries
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
        return new SystemHeatmapContract(ordered.Length, totals, ordered);
    }

    public static IReadOnlyList<ArchElement> Systems(ArchitectureModelInput model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Elements
            .Where(e => e.State == ArchitectureKinds.Implemented && e.Kind == ArchitectureKinds.SystemKind)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, ArchSystemMetadata> MetadataById(ArchitectureModelInput model) =>
        model.Systems.ToDictionary(s => s.ElementId, StringComparer.Ordinal);

    private static ArchSystemMetadata Empty(string elementId) => new(
        elementId, null, [], null, "medium", [], null, null, 0, null, null, null, null, null, null,
        null, false, false, null, [], [], 0, [], null, null);
}
