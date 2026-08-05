using System.Globalization;
using System.Text;
using Harness.SharedKernel.Graph;

namespace Harness.Modules.Workflows.Product.Graph;

/// <summary>
/// O componente de impacto do ContextBundle da chefe (Onda 3.1): compacto (orçamento 1–3k
/// tokens, teto em caracteres com corte DECLARADO), determinístico, com IDs referenciáveis.
/// O delta — o que mudou desde <paramref name="since"/> — vem primeiro, porque é o que muda
/// decisão; o retrato (STALE, versão do grafo) vem depois.
/// </summary>
public static class ProjectImpactDigests
{
    /// <summary>~3k tokens. O corte declara o que ficou de fora.</summary>
    private const int MaxChars = 12_000;

    public static string Build(
        long graphVersion,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        DateTimeOffset? since)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        if (nodes.Count == 0)
        {
            return "O grafo do projeto ainda não tem nós (projeção vazia ou nunca reconstruída).";
        }

        var builder = new StringBuilder();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"Grafo do projeto v{graphVersion.ToString(CultureInfo.InvariantCulture)} — " +
            $"{nodes.Count.ToString(CultureInfo.InvariantCulture)} nós, " +
            $"{edges.Count.ToString(CultureInfo.InvariantCulture)} arestas.");

        if (since is { } cutoff)
        {
            var changed = nodes
                .Where(node => node.UpdatedAt > cutoff)
                .OrderByDescending(node => node.UpdatedAt)
                .ThenBy(node => node.Id, StringComparer.Ordinal)
                .Take(15)
                .ToArray();
            builder.AppendLine();
            builder.AppendLine("MUDANÇAS DESDE O ÚLTIMO TURNO (o delta é o que muda decisão):");
            if (changed.Length == 0)
            {
                builder.AppendLine("- nenhuma mudança de impacto no período.");
            }

            foreach (var node in changed)
            {
                var state = node.State switch
                {
                    GraphNodeState.Stale =>
                        $"STALE por {node.StaleCauseNodeId} v{node.StaleCauseVersion?.ToString(CultureInfo.InvariantCulture)}",
                    GraphNodeState.Retired => "aposentado",
                    _ => $"ativo, v{node.Version.ToString(CultureInfo.InvariantCulture)}",
                };
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {node.Title} ({node.Id}): {state}");
            }
        }

        var staleWork = GraphQueryService.GetStaleWork(nodes);
        builder.AppendLine();
        builder.AppendLine("AGUARDANDO REVALIDAÇÃO:");
        builder.AppendLine(staleWork);

        var text = builder.ToString().TrimEnd();
        return text.Length <= MaxChars
            ? text
            : text[..MaxChars] +
                "\n… [DIGEST TRUNCADO no orçamento — use as consultas do grafo para o restante.]";
    }
}
