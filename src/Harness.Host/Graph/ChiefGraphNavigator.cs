using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Workflows.Product.Graph;
using Harness.Persistence.Abstractions.Graph;

namespace Harness.Host.Graph;

/// <summary>
/// As consultas do grafo expostas à chefe pelo MESMO mecanismo navegável dos anexos (Onda 3.2
/// sobre a Onda 0.7): o grafo aparece como um "arquivo" virtual cujo índice lista as consultas
/// disponíveis, e cada seção pedida executa a consulta e devolve a resposta COMPACTA. A Bruna
/// nunca recebe o grafo bruto — digest no prompt, consulta sob demanda.
/// </summary>
public sealed class ChiefGraphNavigator(
    ProjectGraphProjectionService projection,
    IProjectGraphStore? store) : IChiefAttachmentNavigator
{
    public const string FileName = "grafo-do-projeto";

    private readonly ProjectGraphProjectionService _projection =
        projection ?? throw new ArgumentNullException(nameof(projection));
    private readonly IProjectGraphStore? _store = store;

    public async Task<IReadOnlyList<ChiefAttachmentOutline>> ListOutlinesAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        if (!_projection.Enabled || _store is null)
        {
            return [];
        }

        var snapshot = await _store.GetAsync(tenantId, projectId, cancellationToken);
        if (snapshot.Nodes.Count == 0)
        {
            return [];
        }

        return
        [
            new ChiefAttachmentOutline(
                FileName,
                [
                    new ChiefAttachmentSectionRef(
                        "revalidacao", "Tudo que aguarda revalidação (e por causa de quê)"),
                    new ChiefAttachmentSectionRef(
                        "bloqueio:<id-do-nó>", "Por que este item está bloqueado, até a causa raiz"),
                    new ChiefAttachmentSectionRef(
                        "impacto:<id-do-nó>", "O que é afetado se este item mudar ou atrasar"),
                    new ChiefAttachmentSectionRef(
                        "gate:<id-do-gate>", "O que falta de evidência para o gate"),
                ]),
        ];
    }

    public async Task<string?> ReadSectionAsync(
        string tenantId,
        string projectId,
        string fileName,
        string sectionId,
        CancellationToken cancellationToken = default)
    {
        if (!_projection.Enabled || _store is null ||
            !string.Equals(fileName, FileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var snapshot = await _store.GetAsync(tenantId, projectId, cancellationToken);
        var section = sectionId.Trim();
        if (string.Equals(section, "revalidacao", StringComparison.OrdinalIgnoreCase))
        {
            return GraphQueryService.GetStaleWork(snapshot.Nodes);
        }

        var separator = section.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == section.Length - 1)
        {
            return null;
        }

        var query = section[..separator].ToLowerInvariant();
        var argument = section[(separator + 1)..].Trim();
        return query switch
        {
            "bloqueio" => GraphQueryService.GetBlockingChain(snapshot.Nodes, snapshot.Edges, argument),
            "impacto" => GraphQueryService.GetDownstreamImpact(snapshot.Nodes, snapshot.Edges, argument),
            "gate" => GraphQueryService.GetGateEvidenceGaps(snapshot.Nodes, snapshot.Edges, argument),
            _ => null,
        };
    }
}

/// <summary>
/// Combina navegadores para o turno da chefe: anexos reais da solicitação + o grafo virtual.
/// A ordem é estável e cada fonte é independente — a falha de uma não esconde a outra.
/// </summary>
public sealed class CompositeChiefNavigator(
    IReadOnlyList<IChiefAttachmentNavigator> navigators) : IChiefAttachmentNavigator
{
    private readonly IReadOnlyList<IChiefAttachmentNavigator> _navigators =
        navigators ?? throw new ArgumentNullException(nameof(navigators));

    public async Task<IReadOnlyList<ChiefAttachmentOutline>> ListOutlinesAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        var outlines = new List<ChiefAttachmentOutline>();
        foreach (var navigator in _navigators)
        {
            outlines.AddRange(await navigator.ListOutlinesAsync(tenantId, projectId, cancellationToken));
        }

        return outlines;
    }

    public async Task<string?> ReadSectionAsync(
        string tenantId,
        string projectId,
        string fileName,
        string sectionId,
        CancellationToken cancellationToken = default)
    {
        foreach (var navigator in _navigators)
        {
            var content = await navigator.ReadSectionAsync(
                tenantId, projectId, fileName, sectionId, cancellationToken);
            if (content is not null)
            {
                return content;
            }
        }

        return null;
    }
}
