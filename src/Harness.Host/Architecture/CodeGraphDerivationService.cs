using System.Security.Cryptography;
using System.Text;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;
using Harness.SharedKernel.CodeGraph;
using Harness.SharedKernel.Time;

namespace Harness.Host.Architecture;

/// <summary>Resultado de uma derivação: o grafo em memória e o cabeçalho que ficou gravado.</summary>
public sealed record CodeGraphDerivation(
    CodeGraph Graph,
    CodeGraphSnapshot Snapshot,
    IReadOnlyList<CodeGraphDiagnostic> Diagnostics)
{
    public bool Compiles => Snapshot.ErrorCount == 0;
}

/// <summary>Grafo durável cuja revisão ainda coincide com a árvore publicada.</summary>
public sealed record CurrentCodeGraph(CodeGraph Graph, CodeGraphSnapshot Snapshot);

/// <summary>
/// Deriva o grafo de código, guarda-o por projeto e alimenta o self-map de arquitetura com o que a
/// derivação cobre (B6/F15).
///
/// <b>Sobre "substituir o seed manual onde cobrir".</b> O mapa do Poseidon foi semeado à mão com os
/// elementos (Host, Frontend, Persistência, módulos) e com as relações de CONTINÊNCIA. A derivação
/// não cobre continência — quem contém quem é decisão de organização, não fato do compilador — então
/// essas relações permanecem intocadas. O que a derivação cobre, e o seed manual nunca teve, é
/// DEPENDÊNCIA entre módulos: agora ela sai das referências que existem de fato no código, e não de
/// alguém afirmar que existem.
///
/// Cada relação derivada carrega a propriedade <c>derivedFrom=code-graph</c>. Sem essa marca, um mapa
/// misto viraria indistinguível: ninguém saberia qual seta foi medida e qual foi declarada, e a
/// primeira divergência entre as duas não teria como ser julgada.
///
/// <b>E o que ela não faz:</b> não CRIA elemento de arquitetura. Se um módulo do código não tem
/// elemento correspondente no mapa, a relação é omitida em vez de inventar a ponta que falta — mapa
/// com nó fantasma é pior que mapa incompleto, porque parece completo.
/// </summary>
public sealed class CodeGraphDerivationService(
    ICodeGraphIndex index,
    ICodeGraphStore store,
    IArchitectureStore architecture,
    IClock clock)
{
    private static readonly IReadOnlyDictionary<string, string> DerivedProperties =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["derivedFrom"] = "code-graph"
        };

    private readonly ICodeGraphIndex _index = index ?? throw new ArgumentNullException(nameof(index));
    private readonly ICodeGraphStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly IArchitectureStore _architecture =
        architecture ?? throw new ArgumentNullException(nameof(architecture));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Deriva um grafo transitório sem substituir o índice durável. É usado pelo gate pré-review
    /// sobre a branch da tentativa: código ainda não integrado não pode se passar pelo estado
    /// publicado do projeto.
    /// </summary>
    public Task<CodeGraphBuildResult> InspectAsync(
        string projectId,
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _index.BuildAsync(
            new CodeGraphBuildRequest(projectId, rootPath),
            cancellationToken);
    }

    /// <summary>
    /// Deriva do zero e substitui o índice guardado do par projeto+linguagem.
    /// <paramref name="sourceRevision"/> é a revisão do código indexado — sem ela, um índice velho
    /// fica indistinguível de um atual.
    /// </summary>
    public async Task<CodeGraphDerivation> DeriveAndStoreAsync(
        string tenantId,
        string projectId,
        string rootPath,
        string? sourceRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var result = await _index
            .BuildAsync(new CodeGraphBuildRequest(projectId, rootPath), cancellationToken)
            .ConfigureAwait(false);

        var snapshot = await _store.ReplaceAsync(
            new CodeGraphWrite(
                tenantId,
                projectId,
                _index.Language,
                result.Graph.Digest,
                result.FilesIndexed,
                ScopeOf(result.DiagnosticScope),
                result.Errors.Count,
                sourceRevision,
                _clock.UtcNow,
                [.. result.Graph.Nodes.Select(node => new CodeGraphNodeRow(
                    node.NodeId, node.Symbol, (int)node.Kind, node.FilePath, node.Module))],
                [.. result.Graph.Edges.Select(edge => new CodeGraphEdgeRow(
                    edge.FromNodeId, edge.ToNodeId, (int)edge.Kind))]),
            cancellationToken).ConfigureAwait(false);

        return new CodeGraphDerivation(result.Graph, snapshot, result.Diagnostics);
    }

    /// <summary>
    /// Reconstitui o grafo guardado, para o despacho medir impacto sem derivar de novo. Nulo quando o
    /// projeto nunca foi indexado — e nulo aqui significa "não sei", que é o que os consumidores
    /// tratam como não medido, nunca como sem impacto.
    /// </summary>
    public async Task<CodeGraph?> LoadAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var content = await _store
            .LoadAsync(tenantId, projectId, _index.Language, cancellationToken)
            .ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        return CodeGraph.Create(
            content.Nodes.Select(row => new CodeGraphNode(
                row.NodeId, row.Symbol, (CodeGraphNodeKind)row.Kind, row.FilePath, row.Module)),
            content.Edges.Select(row => new CodeGraphEdge(
                row.FromNodeId, row.ToNodeId, (CodeGraphEdgeKind)row.Kind)));
    }

    /// <summary>
    /// Reusa o índice somente quando a revisão gravada coincide exatamente com a revisão publicada.
    /// Nulo exige reconstrução; um índice velho nunca é apresentado como atual.
    /// </summary>
    public async Task<CurrentCodeGraph?> LoadCurrentAsync(
        string tenantId,
        string projectId,
        string sourceRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        var snapshot = await _store
            .GetSnapshotAsync(tenantId, projectId, _index.Language, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null ||
            !string.Equals(
                snapshot.SourceRevision, sourceRevision, StringComparison.Ordinal))
        {
            return null;
        }

        var graph = await LoadAsync(tenantId, projectId, cancellationToken)
            .ConfigureAwait(false);
        return graph is null ? null : new CurrentCodeGraph(graph, snapshot);
    }

    /// <summary>
    /// Sincroniza no self-map as dependências entre módulos que a derivação apurou. Idempotente: o id
    /// de cada relação é determinístico a partir do par, então rodar duas vezes não duplica.
    /// Relações de uma derivação anterior que já não existem no grafo são removidas; relações manuais
    /// permanecem intocadas. Devolve quantas relações passaram a existir.
    /// </summary>
    public async Task<int> SyncSelfMapAsync(
        string tenantId,
        string projectId,
        CodeGraph graph,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(graph);

        var elements = await _architecture
            .ListElementsAsync(
                tenantId, projectId, ArchitectureKinds.Implemented, null, 1000, cancellationToken)
            .ConfigureAwait(false);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in elements)
        {
            byName.TryAdd(element.Name, element.Id);
        }

        var desired = new Dictionary<string, (string SourceId, string TargetId)>(StringComparer.Ordinal);
        foreach (var (fromModule, toModule, _) in graph.DeriveModuleDependencies())
        {
            if (!TryResolve(byName, fromModule, out var sourceId) ||
                !TryResolve(byName, toModule, out var targetId) ||
                string.Equals(sourceId, targetId, StringComparison.Ordinal))
            {
                // Ponta sem elemento no mapa: omitir é honesto; inventar o nó não é.
                continue;
            }

            var id = DeterministicId(sourceId, targetId);
            desired.TryAdd(id, (sourceId, targetId));
        }

        var existing = await _architecture
            .ListRelationshipsAsync(
                tenantId, projectId, ArchitectureKinds.Implemented, null, 1000, cancellationToken)
            .ConfigureAwait(false);
        foreach (var relationship in existing)
        {
            if (IsCodeGraphDerived(relationship) && !desired.ContainsKey(relationship.Id))
            {
                await _architecture.DeleteRelationshipAsync(
                    tenantId, relationship.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        var created = 0;
        var now = _clock.UtcNow;
        foreach (var (id, endpoints) in desired)
        {
            if (await _architecture.GetRelationshipAsync(tenantId, id, cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                continue;
            }

            await _architecture.CreateRelationshipAsync(
                new ArchitectureRelationshipRecord(
                    tenantId,
                    id,
                    projectId,
                    endpoints.SourceId,
                    endpoints.TargetId,
                    "depends-on",
                    DerivedProperties,
                    ArchitectureKinds.Implemented,
                    1,
                    null,
                    null,
                    null,
                    now,
                    now),
                cancellationToken).ConfigureAwait(false);
            created++;
        }

        return created;
    }

    private static bool IsCodeGraphDerived(ArchitectureRelationshipRecord relationship) =>
        relationship.Properties.TryGetValue("derivedFrom", out var source) &&
        string.Equals(source, "code-graph", StringComparison.Ordinal);

    /// <summary>
    /// Casa o módulo do código com o elemento do mapa. O nome do módulo é o do csproj
    /// (<c>Harness.Modules.Coordination</c>) e o mapa usa o nome curto (<c>Coordination</c>), então as
    /// tentativas vão do mais específico ao mais curto — e param na primeira que existe, sem chute.
    /// </summary>
    private static bool TryResolve(
        Dictionary<string, string> byName,
        string module,
        out string elementId)
    {
        foreach (var candidate in Candidates(module))
        {
            if (byName.TryGetValue(candidate, out var found))
            {
                elementId = found;
                return true;
            }
        }

        elementId = string.Empty;
        return false;
    }

    private static IEnumerable<string> Candidates(string module)
    {
        yield return module;
        const string ModulePrefix = "Harness.Modules.";
        if (module.StartsWith(ModulePrefix, StringComparison.Ordinal))
        {
            yield return module[ModulePrefix.Length..];
        }
    }

    /// <summary>
    /// Id de 26 caracteres no alfabeto Crockford (o formato que a tabela exige), derivado do par de
    /// elementos. Determinístico de propósito: é o que torna a sincronização idempotente sem precisar
    /// de uma tabela de controle à parte.
    /// </summary>
    private static string DeterministicId(string sourceId, string targetId)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"code-graph-depends-on:{sourceId}->{targetId}"));
        var builder = new StringBuilder(26);
        for (var i = 0; i < 26; i++)
        {
            builder.Append(Alphabet[hash[i] & 0x1F]);
        }

        return builder.ToString();
    }

    private static string ScopeOf(CodeGraphDiagnosticScope scope) => scope switch
    {
        CodeGraphDiagnosticScope.Semantic => CodeGraphSnapshot.SemanticScope,
        _ => CodeGraphSnapshot.SyntaxOnlyScope
    };
}
