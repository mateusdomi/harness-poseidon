using System.Globalization;
using System.Text;
using Harness.SharedKernel.Graph;

namespace Harness.Modules.Workflows.Product.Graph;

/// <summary>
/// As consultas da Onda 3.2 — a Bruna CONSULTA o grafo, nunca o recebe bruto. Cada resposta é
/// texto compacto, determinístico e com teto de linhas (o excedente é declarado, nunca
/// silenciosamente cortado). Puro: recebe o snapshot, devolve texto.
/// </summary>
public static class GraphQueryService
{
    private const int MaxLines = 30;

    /// <summary>Por que este item está bloqueado — a cadeia até a causa raiz.</summary>
    public static string GetBlockingChain(
        IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, string nodeId)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!byId.TryGetValue(nodeId, out var start))
        {
            return $"Nó {nodeId} não existe no grafo do projeto.";
        }

        var lines = new List<string> { $"Cadeia de bloqueio de \"{start.Title}\" ({nodeId}):" };
        var seen = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        var frontier = new Queue<(string Id, int Depth)>();
        frontier.Enqueue((nodeId, 0));
        var found = false;
        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            foreach (var edge in edges
                .Where(edge => edge.ValidUntil is null &&
                    edge.Status == GraphEdgeStatus.Accepted &&
                    edge.RelationType is GraphRelationType.DependsOn or GraphRelationType.BlockedBy
                        or GraphRelationType.DerivesFrom &&
                    string.Equals(edge.FromNodeId, current, StringComparison.Ordinal))
                .OrderBy(edge => edge.ToNodeId, StringComparer.Ordinal))
            {
                if (!seen.Add(edge.ToNodeId) || !byId.TryGetValue(edge.ToNodeId, out var target))
                {
                    continue;
                }

                var marker = target.State switch
                {
                    GraphNodeState.Stale =>
                        $" [STALE — causa: {target.StaleCauseNodeId} v{target.StaleCauseVersion?.ToString(CultureInfo.InvariantCulture)}]",
                    GraphNodeState.Retired => " [APOSENTADO]",
                    _ => string.Empty,
                };
                if (marker.Length > 0)
                {
                    found = true;
                }

                lines.Add(
                    $"{new string(' ', (depth + 1) * 2)}└ {Relation(edge.RelationType)} " +
                    $"\"{target.Title}\" ({target.Id}){marker}");
                frontier.Enqueue((edge.ToNodeId, depth + 1));
            }
        }

        if (lines.Count == 1)
        {
            return $"\"{start.Title}\" ({nodeId}) não tem predecessores no grafo — nada o bloqueia estruturalmente.";
        }

        if (!found)
        {
            lines.Add("Nenhum predecessor está STALE ou aposentado — o bloqueio, se houver, não é estrutural.");
        }

        return Cap(lines);
    }

    /// <summary>O que é afetado se este item mudar ou atrasar.</summary>
    public static string GetDownstreamImpact(
        IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, string nodeId)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!byId.TryGetValue(nodeId, out var start))
        {
            return $"Nó {nodeId} não existe no grafo do projeto.";
        }

        var impacted = ImpactAnalysisService.Analyze(nodes, edges, nodeId);
        if (impacted.Count == 0)
        {
            return $"Uma mudança em \"{start.Title}\" ({nodeId}) não afeta nenhum outro item do grafo.";
        }

        var lines = new List<string>
        {
            $"Se \"{start.Title}\" ({nodeId}) mudar ou atrasar, são afetados:",
        };
        foreach (var node in impacted)
        {
            var title = byId.TryGetValue(node.NodeId, out var known) ? known.Title : node.NodeId;
            var kind = node.Classification switch
            {
                ImpactClassification.Direct => "DIRETO",
                ImpactClassification.Transitive => "transitivo",
                _ => "possível (inferência não confirmada)",
            };
            lines.Add($"- {title} ({node.NodeId}) — {kind}, via {string.Join(" → ", node.Path)}");
        }

        return Cap(lines);
    }

    /// <summary>O que falta de evidência para o gate: requisitos do gate sem prova aceita.</summary>
    public static string GetGateEvidenceGaps(
        IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, string gateNodeId)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!byId.TryGetValue(gateNodeId, out var gate))
        {
            return $"Nó {gateNodeId} não existe no grafo do projeto.";
        }

        // O que o gate exige: arestas requires SAINDO do gate. Para cada exigência, existe
        // evidência ATIVA que a prove?
        var required = edges
            .Where(edge => edge.ValidUntil is null &&
                edge.Status == GraphEdgeStatus.Accepted &&
                edge.RelationType == GraphRelationType.Requires &&
                string.Equals(edge.FromNodeId, gateNodeId, StringComparison.Ordinal))
            .Select(edge => edge.ToNodeId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (required.Length == 0)
        {
            return $"O gate \"{gate.Title}\" ({gateNodeId}) não declara exigências no grafo.";
        }

        var lines = new List<string> { $"Evidência do gate \"{gate.Title}\" ({gateNodeId}):" };
        foreach (var requirement in required)
        {
            var title = byId.TryGetValue(requirement, out var known) ? known.Title : requirement;
            var proofs = edges
                .Where(edge => edge.ValidUntil is null &&
                    edge.Status == GraphEdgeStatus.Accepted &&
                    edge.RelationType == GraphRelationType.Proves &&
                    string.Equals(edge.ToNodeId, requirement, StringComparison.Ordinal) &&
                    byId.TryGetValue(edge.FromNodeId, out var proof) &&
                    proof.State == GraphNodeState.Active)
                .Count();
            var requiredNode = byId.GetValueOrDefault(requirement);
            var stale = requiredNode?.State == GraphNodeState.Stale;
            lines.Add(proofs > 0 && !stale
                ? $"- OK: {title} ({requirement}) — {proofs.ToString(CultureInfo.InvariantCulture)} prova(s) ativa(s)"
                : stale
                    ? $"- FALTA: {title} ({requirement}) — STALE, precisa de revalidação"
                    : $"- FALTA: {title} ({requirement}) — nenhuma prova ativa");
        }

        return Cap(lines);
    }

    /// <summary>Tudo que aguarda revalidação, e por causa de quê.</summary>
    public static string GetStaleWork(IReadOnlyList<GraphNode> nodes)
    {
        var stale = nodes
            .Where(node => node.State == GraphNodeState.Stale)
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        if (stale.Length == 0)
        {
            return "Nenhum item do projeto aguarda revalidação.";
        }

        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var lines = new List<string>
        {
            $"{stale.Length.ToString(CultureInfo.InvariantCulture)} item(ns) aguardando revalidação:",
        };
        foreach (var node in stale)
        {
            var causeTitle = node.StaleCauseNodeId is { } causeId &&
                byId.TryGetValue(causeId, out var cause)
                ? cause.Title
                : node.StaleCauseNodeId ?? "(causa não registrada)";
            lines.Add(
                $"- {node.Title} ({node.Id}) — invalidado por \"{causeTitle}\" " +
                $"v{node.StaleCauseVersion?.ToString(CultureInfo.InvariantCulture)}");
        }

        return Cap(lines);
    }

    private static string Relation(GraphRelationType relation) => relation switch
    {
        GraphRelationType.DependsOn => "depende de",
        GraphRelationType.BlockedBy => "bloqueado por",
        GraphRelationType.DerivesFrom => "deriva de",
        _ => relation.ToString().ToLowerInvariant(),
    };

    /// <summary>Teto declarado: o corte diz quanto ficou de fora, nunca é silencioso.</summary>
    private static string Cap(List<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines.Take(MaxLines))
        {
            builder.AppendLine(line);
        }

        if (lines.Count > MaxLines)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"… e mais {(lines.Count - MaxLines).ToString(CultureInfo.InvariantCulture)} linha(s) — refine a consulta para ver o restante.");
        }

        return builder.ToString().TrimEnd();
    }
}
