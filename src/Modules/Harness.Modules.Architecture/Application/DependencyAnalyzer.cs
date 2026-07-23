using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-01 — consulta de dependências: "quem depende de X". PURA e determinística. Uma aresta de
/// dependência S→T (uses/depends-on/calls/flows-to) significa que S DEPENDE de T; portanto os
/// dependentes de X são os elementos que alcançam X por essas arestas (travessia REVERSA). Arestas
/// estruturais 'contains' NÃO são dependência. Retorna dependentes diretos e transitivos, sem ciclos.
/// </summary>
public static class DependencyAnalyzer
{
    public static ArchitectureDependencyQueryContract WhoDependsOn(
        ArchitectureModelInput model, string elementId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);

        var implemented = model.Elements
            .Where(e => e.State == ArchitectureKinds.Implemented)
            .ToDictionary(e => e.Id, StringComparer.Ordinal);
        var target = implemented.GetValueOrDefault(elementId);

        // Arestas reversas: para cada dependência S->T, guarda T -> (S, kind).
        var reverse = new Dictionary<string, List<(string Source, string Kind)>>(StringComparer.Ordinal);
        foreach (var rel in model.Relationships)
        {
            if (rel.State != ArchitectureKinds.Implemented ||
                !ArchitectureKinds.IsDependencyEdge(rel.Kind))
            {
                continue;
            }

            if (!reverse.TryGetValue(rel.TargetId, out var list))
            {
                list = [];
                reverse[rel.TargetId] = list;
            }

            list.Add((rel.SourceId, rel.Kind));
        }

        var dependents = new List<ArchitectureDependentContract>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { elementId };
        var direct = new HashSet<string>(StringComparer.Ordinal);

        // BFS reversa a partir do alvo. O primeiro nível é "direto"; os demais, transitivos.
        var frontier = new Queue<(string Id, int Depth)>();
        frontier.Enqueue((elementId, 0));
        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            if (!reverse.TryGetValue(current, out var sources))
            {
                continue;
            }

            foreach (var (source, via) in sources.OrderBy(s => s.Source, StringComparer.Ordinal))
            {
                if (depth == 0)
                {
                    direct.Add(source);
                }

                if (!seen.Add(source))
                {
                    continue;
                }

                if (implemented.TryGetValue(source, out var element))
                {
                    dependents.Add(new ArchitectureDependentContract(
                        element.Id, element.Name, element.Kind, via, depth == 0));
                }

                frontier.Enqueue((source, depth + 1));
            }
        }

        var ordered = dependents
            .OrderByDescending(d => d.Direct)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.ElementId, StringComparer.Ordinal)
            .ToArray();

        return new ArchitectureDependencyQueryContract(
            elementId,
            target?.Name ?? elementId,
            ordered.Count(d => d.Direct),
            ordered.Count(d => !d.Direct),
            ordered);
    }
}
