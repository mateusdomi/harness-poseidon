using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Harness.SharedKernel.CodeGraph;

/// <summary>O que um nó representa. Sempre derivado do código — nada aqui é declarado à mão.</summary>
public enum CodeGraphNodeKind
{
    /// <summary>Tipo nomeado: classe, record, struct, interface ou enum.</summary>
    Type = 0,

    /// <summary>Agrupamento estrutural real (o projeto/diretório de módulo que contém os tipos).</summary>
    Module = 1
}

public enum CodeGraphEdgeKind
{
    /// <summary>A origem menciona o destino na própria assinatura ou no corpo.</summary>
    References = 0,

    /// <summary>Herança de classe ou implementação de interface.</summary>
    Inherits = 1,

    /// <summary>O módulo contém o tipo.</summary>
    Contains = 2
}

/// <summary>
/// Um nó do grafo. <paramref name="NodeId"/> é o nome de metadados totalmente qualificado do
/// símbolo — estável entre reconstruções, que é o que permite comparar dois índices.
/// </summary>
public sealed record CodeGraphNode(
    string NodeId,
    string Symbol,
    CodeGraphNodeKind Kind,
    string FilePath,
    string Module);

/// <summary>
/// Uma aresta. A direção é sempre "quem depende" → "de quem depende": <paramref name="FromNodeId"/>
/// deixa de compilar se <paramref name="ToNodeId"/> mudar. Os DEPENDENTES de um nó são, portanto, a
/// travessia inversa — e é essa direção que mede impacto.
/// </summary>
public sealed record CodeGraphEdge(
    string FromNodeId,
    string ToNodeId,
    CodeGraphEdgeKind Kind);

/// <summary>
/// Raio de impacto de um conjunto de caminhos tocados.
///
/// O campo que dá honestidade a este registro é <see cref="UnindexedPaths"/>. Um caminho que o
/// índice não cobre não tem impacto ZERO: tem impacto DESCONHECIDO, e as duas coisas jamais podem
/// aparecer com o mesmo número. Sem essa distinção, a segunda entrega desta fase (o servidor TS,
/// que ainda não indexa o frontend) faria todo card de frontend parecer inofensivo — que é
/// exatamente o contrário da verdade.
/// </summary>
public sealed record CodeBlastRadius(
    IReadOnlyList<string> TouchedNodeIds,
    IReadOnlyList<string> DependentNodeIds,
    IReadOnlyList<string> UnindexedPaths)
{
    public static CodeBlastRadius Nothing { get; } = new([], [], []);

    /// <summary>Quantos nós quebram se o que foi tocado mudar. É o "N dependentes" do B6.</summary>
    public int DependentCount => DependentNodeIds.Count;

    /// <summary>
    /// Verdadeiro só quando TODO caminho tocado está no índice. Falso significa "não sei", nunca
    /// "não impacta".
    /// </summary>
    public bool IsFullyMeasured => UnindexedPaths.Count == 0;
}

/// <summary>
/// Grafo de código derivado e reconstruível (B6).
///
/// Duas propriedades sustentam tudo o que é construído sobre ele:
///
/// <b>Derivado</b> — nenhum nó ou aresta é cadastrado por pessoa nem por agente. Isso é o que
/// diferencia este grafo do mapa de arquitetura semeado à mão: um mapa declarado envelhece em
/// silêncio e continua parecendo verdade; um grafo derivado erra junto com o código, e reconstruir
/// corrige.
///
/// <b>Determinístico</b> — a mesma árvore de fontes produz o mesmo <see cref="Digest"/>. Sem isso
/// não existe a pergunta "o grafo mudou?", e sem essa pergunta o índice não serve de gate: qualquer
/// divergência poderia ser atribuída à ordem em que os arquivos foram lidos.
///
/// O grafo atravessa APENAS o que foi indexado. Arestas que apontam para fora (tipos da BCL, de
/// pacote, de outra linguagem) são descartadas na construção de propósito: inventar nó para símbolo
/// que ninguém indexou encheria o grafo de folhas que nunca poderiam ser reconstruídas nem
/// verificadas.
/// </summary>
public sealed class CodeGraph
{
    private readonly Dictionary<string, CodeGraphNode> _byId;
    private readonly Dictionary<string, string[]> _dependentsOf;
    private readonly Dictionary<string, string[]> _nodesByFile;

    private CodeGraph(
        IReadOnlyList<CodeGraphNode> nodes,
        IReadOnlyList<CodeGraphEdge> edges,
        Dictionary<string, CodeGraphNode> byId,
        Dictionary<string, string[]> dependentsOf,
        Dictionary<string, string[]> nodesByFile,
        string digest)
    {
        Nodes = nodes;
        Edges = edges;
        _byId = byId;
        _dependentsOf = dependentsOf;
        _nodesByFile = nodesByFile;
        Digest = digest;
    }

    public static CodeGraph Empty { get; } = Create([], []);

    public IReadOnlyList<CodeGraphNode> Nodes { get; }

    public IReadOnlyList<CodeGraphEdge> Edges { get; }

    /// <summary>
    /// Impressão digital do conteúdo do grafo. Igual ⇒ mesma estrutura de código indexada; é a prova
    /// de que a reconstrução do zero chegou ao mesmo lugar.
    /// </summary>
    public string Digest { get; }

    public static CodeGraph Create(
        IEnumerable<CodeGraphNode> nodes,
        IEnumerable<CodeGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        // Um símbolo `partial` é declarado em vários arquivos. Ele é UM nó (senão as arestas não
        // teriam alvo único), mas TODOS os arquivos que o declaram precisam mapear para ele: se o
        // segundo arquivo não mapeasse, tocá-lo seria lido como "caminho fora do índice" e o impacto
        // apareceria como desconhecido num arquivo que o índice conhece perfeitamente.
        var normalizedNodes = nodes
            .Select(Normalize)
            .OrderBy(node => node.NodeId, StringComparer.Ordinal)
            .ThenBy(node => node.FilePath, StringComparer.Ordinal)
            .ToArray();
        var orderedNodes = normalizedNodes
            .DistinctBy(node => node.NodeId, StringComparer.Ordinal)
            .ToArray();
        var byId = orderedNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);

        var orderedEdges = edges
            .Select(Normalize)
            // Aresta que sai do índice não vira nó novo: o grafo só afirma sobre o que indexou.
            .Where(edge => byId.ContainsKey(edge.FromNodeId) && byId.ContainsKey(edge.ToNodeId))
            .Where(edge => !string.Equals(edge.FromNodeId, edge.ToNodeId, StringComparison.Ordinal))
            .DistinctBy(
                edge => (edge.FromNodeId, edge.ToNodeId, edge.Kind),
                EdgeKeyComparer.Instance)
            .OrderBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal)
            .ThenBy(edge => (int)edge.Kind)
            .ToArray();

        var dependents = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var edge in orderedEdges)
        {
            if (edge.Kind == CodeGraphEdgeKind.Contains)
            {
                // Conter não é depender: o módulo agrupa o tipo, não quebra com ele.
                continue;
            }

            if (!dependents.TryGetValue(edge.ToNodeId, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                dependents[edge.ToNodeId] = set;
            }

            set.Add(edge.FromNodeId);
        }

        var nodesByFile = normalizedNodes
            .Where(node => node.FilePath.Length > 0)
            .GroupBy(node => node.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(node => node.NodeId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new CodeGraph(
            orderedNodes,
            orderedEdges,
            byId,
            dependents.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
            nodesByFile,
            ComputeDigest(orderedNodes, orderedEdges));
    }

    public CodeGraphNode? Find(string nodeId) =>
        _byId.GetValueOrDefault(nodeId?.Trim() ?? string.Empty);

    /// <summary>Nós declarados no arquivo, ou vazio quando o índice não cobre o caminho.</summary>
    public IReadOnlyList<string> NodesInFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return _nodesByFile.GetValueOrDefault(NormalizePath(filePath)) ?? [];
    }

    /// <summary>
    /// Todos os nós que quebram — direta ou indiretamente — se <paramref name="nodeId"/> mudar.
    /// Travessia inversa, transitiva e à prova de ciclo (código real tem dependências mútuas, e um
    /// ciclo não pode virar laço infinito num gate).
    /// </summary>
    public IReadOnlyList<string> DependentsOf(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        var start = nodeId.Trim();
        if (!_byId.ContainsKey(start))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!_dependentsOf.TryGetValue(current, out var direct))
            {
                continue;
            }

            foreach (var dependent in direct)
            {
                if (seen.Add(dependent))
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        seen.Remove(start);
        return [.. seen.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Mede o impacto de tocar <paramref name="touchedFilePaths"/>. Caminho sem nó no índice sai em
    /// <see cref="CodeBlastRadius.UnindexedPaths"/> — o índice diz "não sei", e quem consome decide
    /// o que fazer com o não sei; aqui ele nunca é convertido em zero.
    /// </summary>
    public CodeBlastRadius MeasureBlastRadius(IReadOnlyCollection<string> touchedFilePaths)
    {
        ArgumentNullException.ThrowIfNull(touchedFilePaths);

        var touched = new SortedSet<string>(StringComparer.Ordinal);
        var unindexed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in touchedFilePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var normalized = NormalizePath(path);
            var nodes = NodesInFile(normalized);
            if (nodes.Count == 0)
            {
                unindexed.Add(normalized);
                continue;
            }

            foreach (var node in nodes)
            {
                touched.Add(node);
            }
        }

        var dependents = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in touched)
        {
            foreach (var dependent in DependentsOf(node))
            {
                // Quem foi tocado já está contado como alvo; dependente é o que vem DEPOIS dele.
                if (!touched.Contains(dependent))
                {
                    dependents.Add(dependent);
                }
            }
        }

        return new CodeBlastRadius([.. touched], [.. dependents], [.. unindexed]);
    }

    /// <summary>
    /// Agrega as arestas de tipo em dependências entre MÓDULOS. É o que alimenta o self-map do
    /// módulo Architecture por derivação: em vez de alguém afirmar "o Host depende da Persistência",
    /// a afirmação passa a sair das referências que existem de fato.
    /// </summary>
    public IReadOnlyList<(string FromModule, string ToModule, int EdgeCount)> DeriveModuleDependencies()
    {
        var moduleOf = Nodes
            .Where(node => node.Kind == CodeGraphNodeKind.Type && node.Module.Length > 0)
            .ToDictionary(node => node.NodeId, node => node.Module, StringComparer.Ordinal);

        return Edges
            .Where(edge => edge.Kind != CodeGraphEdgeKind.Contains)
            .Select(edge => (
                From: moduleOf.GetValueOrDefault(edge.FromNodeId),
                To: moduleOf.GetValueOrDefault(edge.ToNodeId)))
            .Where(pair =>
                pair.From is { Length: > 0 } &&
                pair.To is { Length: > 0 } &&
                !string.Equals(pair.From, pair.To, StringComparison.Ordinal))
            .GroupBy(pair => pair, TupleComparer.Instance)
            .Select(group => (group.Key.From!, group.Key.To!, group.Count()))
            .OrderBy(item => item.Item1, StringComparer.Ordinal)
            .ThenBy(item => item.Item2, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ComputeDigest(
        IReadOnlyList<CodeGraphNode> nodes,
        IReadOnlyList<CodeGraphEdge> edges)
    {
        // Separador de unidade: caractere de controle que não ocorre em nome de símbolo nem em
        // caminho, então nenhum conteúdo consegue imitar a fronteira entre dois campos.
        const char Separator = '\u001f';
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            builder.Append('N').Append(Separator)
                .Append(node.NodeId).Append(Separator)
                .Append(((int)node.Kind).ToString(CultureInfo.InvariantCulture)).Append(Separator)
                .Append(node.FilePath).Append(Separator)
                .Append(node.Module).Append('\n');
        }

        foreach (var edge in edges)
        {
            builder.Append('E').Append(Separator)
                .Append(edge.FromNodeId).Append(Separator)
                .Append(edge.ToNodeId).Append(Separator)
                .Append(((int)edge.Kind).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static CodeGraphNode Normalize(CodeGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(node.NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(node.Symbol);

        return node with
        {
            NodeId = node.NodeId.Trim(),
            Symbol = node.Symbol.Trim(),
            FilePath = NormalizePath(node.FilePath ?? string.Empty),
            Module = (node.Module ?? string.Empty).Trim()
        };
    }

    private static CodeGraphEdge Normalize(CodeGraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.FromNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.ToNodeId);

        return edge with
        {
            FromNodeId = edge.FromNodeId.Trim(),
            ToNodeId = edge.ToNodeId.Trim()
        };
    }

    /// <summary>
    /// Caminho relativo com separador '/' e sem './'. Sem isto, o mesmo arquivo citado por um card
    /// como <c>src\\X.cs</c> e indexado como <c>src/X.cs</c> mediria impacto zero — o pior modo de
    /// falhar, porque parece resposta.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    private sealed class EdgeKeyComparer
        : IEqualityComparer<(string FromNodeId, string ToNodeId, CodeGraphEdgeKind Kind)>
    {
        public static EdgeKeyComparer Instance { get; } = new();

        public bool Equals(
            (string FromNodeId, string ToNodeId, CodeGraphEdgeKind Kind) x,
            (string FromNodeId, string ToNodeId, CodeGraphEdgeKind Kind) y) =>
            string.Equals(x.FromNodeId, y.FromNodeId, StringComparison.Ordinal) &&
            string.Equals(x.ToNodeId, y.ToNodeId, StringComparison.Ordinal) &&
            x.Kind == y.Kind;

        public int GetHashCode((string FromNodeId, string ToNodeId, CodeGraphEdgeKind Kind) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.FromNodeId),
                StringComparer.Ordinal.GetHashCode(value.ToNodeId),
                (int)value.Kind);
    }

    private sealed class TupleComparer : IEqualityComparer<(string? From, string? To)>
    {
        public static TupleComparer Instance { get; } = new();

        public bool Equals((string? From, string? To) x, (string? From, string? To) y) =>
            string.Equals(x.From, y.From, StringComparison.Ordinal) &&
            string.Equals(x.To, y.To, StringComparison.Ordinal);

        public int GetHashCode((string? From, string? To) value) =>
            HashCode.Combine(value.From ?? string.Empty, value.To ?? string.Empty);
    }
}
