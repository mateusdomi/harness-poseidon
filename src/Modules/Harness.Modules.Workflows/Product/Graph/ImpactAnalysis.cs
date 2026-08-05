using Harness.SharedKernel.Graph;

namespace Harness.Modules.Workflows.Product.Graph;

public enum ImpactClassification
{
    /// <summary>Alcançado em UM salto por aresta Accepted a partir da causa.</summary>
    Direct,

    /// <summary>Alcançado por mais de um salto, todos por arestas Accepted.</summary>
    Transitive,

    /// <summary>
    /// Alcançado somente por caminho que atravessa aresta Proposed. INFORMATIVO: nunca marca
    /// STALE, nunca bloqueia readiness — apenas aparece no digest.
    /// </summary>
    Possible,
}

/// <summary>Um nó atingido pela mudança, com o caminho que explica o porquê.</summary>
public sealed record ImpactedNode(
    string NodeId,
    ImpactClassification Classification,
    IReadOnlyList<string> Path);

/// <summary>
/// A travessia determinística de impacto da Onda 2.1: dado o nó que MUDOU, quais nós ficam com a
/// premissa invalidada.
///
/// A direção do impacto é semântica da relação, não da seta. `card implements requirement` aponta
/// card→requirement; a mudança no REQUISITO é que atinge o card. Relações de dependência
/// propagam contra a seta (quem aponta é atingido); relações de efeito (`impacts`, `invalidates`,
/// `supersedes`, `produces`) propagam a favor.
///
/// Controle de falso positivo (Onda 2.4): nó sem aresta de impacto não propaga NADA — falso
/// positivo é defeito da mesma severidade que falso negativo, porque STALE ruidoso destrói a
/// confiança no mecanismo inteiro.
/// </summary>
public static class ImpactAnalysisService
{
    /// <summary>Relações em que a mudança no destino (`to`) atinge a origem (`from`).</summary>
    private static readonly HashSet<GraphRelationType> AgainstArrow =
    [
        GraphRelationType.Implements,
        GraphRelationType.Verifies,
        GraphRelationType.Proves,
        GraphRelationType.DerivesFrom,
        GraphRelationType.ConstrainedBy,
        GraphRelationType.DependsOn,
        GraphRelationType.BlockedBy,
        GraphRelationType.Requires,
    ];

    /// <summary>Relações em que a mudança na origem (`from`) atinge o destino (`to`).</summary>
    private static readonly HashSet<GraphRelationType> WithArrow =
    [
        GraphRelationType.Impacts,
        GraphRelationType.Invalidates,
        GraphRelationType.Supersedes,
        GraphRelationType.Produces,
    ];

    public static IReadOnlyList<ImpactedNode> Analyze(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        string causeNodeId)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentException.ThrowIfNullOrWhiteSpace(causeNodeId);

        var known = nodes.Where(node => node.State != GraphNodeState.Retired)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!known.Contains(causeNodeId))
        {
            return [];
        }

        // Adjacência de impacto: para cada nó, quem ele atinge quando muda — com o status da
        // aresta que carrega o impacto. Aresta expirada (valid_until) não propaga.
        var reach = new Dictionary<string, List<(string Target, GraphEdgeStatus Status)>>(
            StringComparer.Ordinal);
        foreach (var edge in edges.Where(edge => edge.ValidUntil is null))
        {
            if (!known.Contains(edge.FromNodeId) || !known.Contains(edge.ToNodeId))
            {
                continue;
            }

            if (AgainstArrow.Contains(edge.RelationType))
            {
                Add(reach, edge.ToNodeId, edge.FromNodeId, edge.Status);
            }

            if (WithArrow.Contains(edge.RelationType))
            {
                Add(reach, edge.FromNodeId, edge.ToNodeId, edge.Status);
            }
        }

        // BFS determinístico. Um nó pode ser alcançável por caminho Accepted E por caminho com
        // Proposed: vale o MELHOR caminho (Accepted vence Proposed; menos saltos vence mais).
        var best = new Dictionary<string, (int Hops, bool ViaProposed, IReadOnlyList<string> Path)>(
            StringComparer.Ordinal);
        var queue = new Queue<(string Node, int Hops, bool ViaProposed, IReadOnlyList<string> Path)>();
        queue.Enqueue((causeNodeId, 0, false, [causeNodeId]));

        while (queue.Count > 0)
        {
            var (current, hops, viaProposed, path) = queue.Dequeue();
            if (!reach.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var (target, status) in targets
                .OrderBy(entry => entry.Target, StringComparer.Ordinal))
            {
                if (string.Equals(target, causeNodeId, StringComparison.Ordinal))
                {
                    continue;
                }

                var nextViaProposed = viaProposed || status == GraphEdgeStatus.Proposed;
                var candidate = (hops + 1, nextViaProposed, (IReadOnlyList<string>)[.. path, target]);
                if (best.TryGetValue(target, out var existing) &&
                    (existing.ViaProposed, existing.Hops).CompareTo(
                        (nextViaProposed, hops + 1)) <= 0)
                {
                    continue;
                }

                best[target] = candidate;
                queue.Enqueue((target, hops + 1, nextViaProposed, candidate.Item3));
            }
        }

        return [.. best
            .Select(entry => new ImpactedNode(
                entry.Key,
                entry.Value.ViaProposed
                    ? ImpactClassification.Possible
                    : entry.Value.Hops == 1
                        ? ImpactClassification.Direct
                        : ImpactClassification.Transitive,
                entry.Value.Path))
            .OrderBy(node => node.NodeId, StringComparer.Ordinal)];
    }

    private static void Add(
        Dictionary<string, List<(string, GraphEdgeStatus)>> reach,
        string source,
        string target,
        GraphEdgeStatus status)
    {
        if (!reach.TryGetValue(source, out var list))
        {
            reach[source] = list = [];
        }

        list.Add((target, status));
    }
}
