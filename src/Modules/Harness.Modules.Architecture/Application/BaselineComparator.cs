using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-10 — lógica PURA da integração Delivery↔Architecture. Congela o modelo VIGENTE num snapshot
/// (baseline/as-built), compara proposta × implementação no encerramento e encontra candidatos de reuso
/// no portfólio antes de uma nova entrega. Tudo determinístico e derivado estritamente dos fatos gravados.
/// </summary>
public static class BaselineComparator
{
    /// <summary>Congela o modelo VIGENTE (elementos + arestas implementadas) numa foto imutável.</summary>
    public static ArchitectureModelSnapshot Snapshot(ArchitectureModelInput model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var elements = model.Elements
            .Where(e => e.State == ArchitectureKinds.Implemented)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new ArchitectureSnapshotElement(e.Id, e.Kind, e.Name))
            .ToArray();
        var edges = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented)
            .OrderBy(r => r.SourceId, StringComparer.Ordinal)
            .ThenBy(r => r.TargetId, StringComparer.Ordinal)
            .Select(r => new ArchitectureSnapshotEdge(r.SourceId, r.TargetId, r.Kind))
            .ToArray();
        return new ArchitectureModelSnapshot(elements, edges);
    }

    /// <summary>
    /// Compara a arquitetura PROPOSTA (baseline) com a IMPLEMENTADA (as-built): elementos casados,
    /// ausentes (planejados e não construídos) e não planejados (construídos fora da baseline), idem
    /// arestas, mais um percentual de conformidade honesto. Encerramento (ARC-10).
    /// </summary>
    public static BaselineComparisonContract Compare(
        string baselineId, ArchitectureModelSnapshot baseline, ArchitectureModelSnapshot asBuilt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineId);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(asBuilt);

        var baselineElements = baseline.Elements.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var asBuiltElements = asBuilt.Elements.ToDictionary(e => e.Id, StringComparer.Ordinal);

        var drifts = new List<BaselineDriftEntryContract>();
        var matched = 0;
        var missing = 0;
        foreach (var (id, element) in baselineElements.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (asBuiltElements.ContainsKey(id))
            {
                matched++;
            }
            else
            {
                missing++;
                drifts.Add(new BaselineDriftEntryContract(id, element.Name, element.Kind, "missing"));
            }
        }

        var unplanned = 0;
        foreach (var (id, element) in asBuiltElements.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!baselineElements.ContainsKey(id))
            {
                unplanned++;
                drifts.Add(new BaselineDriftEntryContract(id, element.Name, element.Kind, "unplanned"));
            }
        }

        var baselineEdges = baseline.Edges.Select(EdgeKey).ToHashSet(StringComparer.Ordinal);
        var asBuiltEdges = asBuilt.Edges.Select(EdgeKey).ToHashSet(StringComparer.Ordinal);
        var edgeMatched = baselineEdges.Count(asBuiltEdges.Contains);
        var edgeMissing = baselineEdges.Count(e => !asBuiltEdges.Contains(e));
        var edgeUnplanned = asBuiltEdges.Count(e => !baselineEdges.Contains(e));

        var plannedTotal = baselineElements.Count + baselineEdges.Count;
        var conformant = matched + edgeMatched;
        var conformance = plannedTotal == 0 ? 100.0 : Math.Round(100.0 * conformant / plannedTotal, 1);

        return new BaselineComparisonContract(
            baselineId, matched, missing, unplanned, edgeMatched, edgeMissing, edgeUnplanned,
            conformance, drifts);
    }

    /// <summary>
    /// Consulta de reuso ao portfólio ANTES de uma nova entrega: sistemas existentes que já cobrem a
    /// capacidade/domínio pedido (candidatos a reuso) e possíveis duplicidades já marcadas (ARC-10).
    /// </summary>
    public static PortfolioReuseContract FindReuse(
        ArchitectureModelInput model, string? capability, string? domain)
    {
        ArgumentNullException.ThrowIfNull(model);
        var systems = SystemsMapProjector.Systems(model);
        var metadataById = model.Systems.ToDictionary(s => s.ElementId, StringComparer.Ordinal);
        var wantCapability = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        var wantDomain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();

        var candidates = new List<PortfolioReuseCandidateContract>();
        foreach (var system in systems)
        {
            var meta = metadataById.GetValueOrDefault(system.Id);
            var caps = meta?.Capabilities ?? [];

            var matchedCaps = wantCapability is null
                ? []
                : caps.Where(c => string.Equals(c, wantCapability, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            var capOk = wantCapability is null || matchedCaps.Length > 0;
            var domainOk = wantDomain is null ||
                (meta?.Domain is { } d && string.Equals(d, wantDomain, StringComparison.OrdinalIgnoreCase));

            // Precisa casar os filtros informados; sem filtro nenhum, não retorna todo o portfólio.
            if ((wantCapability is null && wantDomain is null) || !capOk || !domainOk)
            {
                continue;
            }

            candidates.Add(new PortfolioReuseCandidateContract(
                system.Id, system.Name, meta?.Domain,
                matchedCaps.Length > 0 ? matchedCaps : caps.ToArray(),
                !string.IsNullOrWhiteSpace(meta?.DuplicateOfId)));
        }

        var ordered = candidates
            .OrderByDescending(c => c.MatchedCapabilities.Count)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PortfolioReuseContract(wantCapability, wantDomain, ordered.Length, ordered);
    }

    private static string EdgeKey(ArchitectureSnapshotEdge edge) =>
        $"{edge.SourceId}{edge.TargetId}{edge.Kind}";
}
