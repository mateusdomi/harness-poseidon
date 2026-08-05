using Harness.SharedKernel.Graph;

namespace Harness.Modules.Workflows.Product.Graph;

/// <summary>
/// Um item de fonte canônica, já normalizado por quem conhece a store de origem (o Host).
/// O projetor não lê banco: recebe FATOS e produz grafo — é o que o mantém determinístico,
/// puro e testável sem infraestrutura.
/// </summary>
public sealed record GraphSourceItem(
    GraphNodeType Type,
    string SourceId,
    string SourceKind,
    int Version,
    string Title,
    bool Retired = false);

/// <summary>
/// Um vínculo entre dois itens de fonte. <see cref="Structural"/> verdadeiro é um fato canônico
/// (card implementa demanda, evidência prova card); falso é inferência de modelo e nasce
/// Proposed com a confiança dada.
/// </summary>
public sealed record GraphSourceLink(
    GraphRelationType Relation,
    GraphNodeType FromType,
    string FromSourceId,
    GraphNodeType ToType,
    string ToSourceId,
    bool Structural = true,
    double Confidence = 1.0);

/// <summary>O estado completo das fontes de um projeto num instante.</summary>
public sealed record GraphSourceSnapshot(
    string ProjectId,
    IReadOnlyList<GraphSourceItem> Items,
    IReadOnlyList<GraphSourceLink> Links);

/// <summary>Evento incremental: um item mudou (com seus vínculos de SAÍDA) ou saiu de cena.</summary>
public abstract record GraphSourceEvent;

public sealed record GraphItemUpserted(
    GraphSourceItem Item,
    IReadOnlyList<GraphSourceLink> OutgoingLinks) : GraphSourceEvent;

public sealed record GraphItemRetired(GraphNodeType Type, string SourceId) : GraphSourceEvent;

public sealed record ProjectGraphProjection(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// O projetor da Onda 1. Duas portas de entrada, um único resultado possível:
///
/// - <see cref="Project"/> reconstrói o grafo INTEIRO a partir do snapshot das fontes
///   (é o `graph rebuild`);
/// - <see cref="Apply"/> avança o snapshot por um evento e devolve o snapshot novo — a projeção
///   incremental é <c>Project(Apply(...))</c> sobre o estado acumulado.
///
/// A equivalência rebuild ≡ incremental não é esperança: é construção. Ids de nó e de aresta são
/// determinísticos, a ordenação é total e estável, e timestamps de criação vêm do relógio de
/// QUEM PERSISTE (a store preserva created_at de ids já existentes) — nada aqui depende de
/// ordem de chegada.
/// </summary>
public static class ProjectGraphProjector
{
    public static ProjectGraphProjection Project(GraphSourceSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Um item por (tipo, fonte): o último declarado vence — é o contrato de idempotência
        // (reprocessar o mesmo snapshot ou o mesmo evento nunca duplica nó).
        var items = new Dictionary<string, GraphSourceItem>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            items[GraphNode.DeterministicId(item.Type, item.SourceId)] = item;
        }

        var nodes = items.Values
            .Select(item => new GraphNode(
                GraphNode.DeterministicId(item.Type, item.SourceId),
                snapshot.ProjectId,
                item.Type,
                item.SourceId,
                item.SourceKind,
                item.Version,
                item.Retired ? GraphNodeState.Retired : GraphNodeState.Active,
                GraphProvenance.Deterministic,
                1.0,
                item.Title,
                now,
                now))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();

        var nodeIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var edges = snapshot.Links
            .Select(link => ToEdge(link, now))
            // Aresta para nó que não existe não é "quase certa": é lixo que quebraria toda
            // travessia. Fica fora, deterministicamente.
            .Where(edge => nodeIds.Contains(edge.FromNodeId) && nodeIds.Contains(edge.ToNodeId))
            .GroupBy(edge => edge.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();

        return new ProjectGraphProjection(nodes, edges);
    }

    /// <summary>
    /// Avança o snapshot das fontes por UM evento, idempotentemente: upsert substitui o item e
    /// TODOS os vínculos de saída dele (o evento carrega o estado novo completo, não um delta de
    /// arestas — deltas de arestas é como incremental e rebuild divergem).
    /// </summary>
    public static GraphSourceSnapshot Apply(GraphSourceSnapshot snapshot, GraphSourceEvent sourceEvent)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sourceEvent);

        switch (sourceEvent)
        {
            case GraphItemUpserted upserted:
            {
                var key = GraphNode.DeterministicId(upserted.Item.Type, upserted.Item.SourceId);
                var items = snapshot.Items
                    .Where(item => !string.Equals(
                        GraphNode.DeterministicId(item.Type, item.SourceId), key, StringComparison.Ordinal))
                    .Append(upserted.Item)
                    .ToArray();
                var links = snapshot.Links
                    .Where(link => !(link.FromType == upserted.Item.Type &&
                        string.Equals(link.FromSourceId, upserted.Item.SourceId, StringComparison.Ordinal)))
                    .Concat(upserted.OutgoingLinks)
                    .ToArray();
                return snapshot with { Items = items, Links = links };
            }

            case GraphItemRetired retired:
            {
                // Aposentar NÃO apaga: o nó fica Retired e as arestas dele permanecem — a
                // história de por que algo dependia de algo é exatamente o que a perícia do run
                // de empréstimos não tinha. Remoção física só existe no rebuild de uma fonte
                // que não declara mais o item.
                var items = snapshot.Items
                    .Select(item => item.Type == retired.Type &&
                        string.Equals(item.SourceId, retired.SourceId, StringComparison.Ordinal)
                        ? item with { Retired = true }
                        : item)
                    .ToArray();
                return snapshot with { Items = items };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(sourceEvent));
        }
    }

    private static GraphEdge ToEdge(GraphSourceLink link, DateTimeOffset now)
    {
        var from = GraphNode.DeterministicId(link.FromType, link.FromSourceId);
        var to = GraphNode.DeterministicId(link.ToType, link.ToSourceId);
        return link.Structural
            ? GraphEdge.Structural(from, to, link.Relation, now)
            : GraphEdge.Proposed(from, to, link.Relation, link.Confidence, now);
    }
}
