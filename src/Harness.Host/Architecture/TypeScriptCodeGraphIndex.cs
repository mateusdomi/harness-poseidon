using System.Text.RegularExpressions;
using Harness.SharedKernel.CodeGraph;

namespace Harness.Host.Architecture;

/// <summary>
/// Índice de código do frontend (B6/F15, segunda entrega).
///
/// Por que não um servidor de linguagem: o custo de manter um processo `tsc` vivo por projeto,
/// alimentá-lo e sincronizá-lo é alto, e a fase autoriza dependência nova apenas para os pacotes
/// Roslyn. O que o consumidor do grafo realmente precisa é a <b>relação de dependência entre
/// arquivos</b> — quem importa quem — para responder "este caminho tem quantos dependentes?".
/// Essa relação está declarada no próprio código, nos imports, e é derivável sem type-checker.
///
/// O limite é declarado, não escondido: este índice enxerga dependência de MÓDULO (arquivo →
/// arquivo), não de símbolo. Ele não sabe que `Button` mudou de assinatura; sabe que 14 arquivos
/// importam `design-system`. Para raio de impacto — que é o consumidor desta fase — a granularidade
/// de arquivo é a que importa, e é honesta: um arquivo indexado deixa de ser <c>UnindexedPath</c>,
/// e um caminho que este índice não cobre continua sendo impacto DESCONHECIDO, nunca zero.
/// </summary>
public sealed partial class TypeScriptCodeGraphIndex : ICodeGraphIndex
{
    private static readonly string[] SourceExtensions = [".ts", ".tsx"];

    private static readonly string[] DefaultExclusions =
        ["node_modules", "dist", "build", "coverage", ".git", "storybook-static", "test-results"];

    public string Language => "typescript";

    public Task<CodeGraphBuildResult> BuildAsync(
        CodeGraphBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = Path.GetFullPath(request.RootPath);
        if (!Directory.Exists(root))
        {
            return Task.FromResult(new CodeGraphBuildResult(
                CodeGraph.Empty,
                [new CodeGraphDiagnostic(
                    "codegraph.ts.root_missing",
                    CodeGraphDiagnosticSeverity.Error,
                    $"A raiz '{request.RootPath}' não existe.")],
                0));
        }

        var excluded = new HashSet<string>(
            request.ExcludedDirectorySegments ?? DefaultExclusions,
            StringComparer.OrdinalIgnoreCase);
        foreach (var segment in DefaultExclusions)
        {
            excluded.Add(segment);
        }

        var files = Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsExcluded(file, root, excluded))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

        var nodes = new List<CodeGraphNode>();
        var edges = new List<CodeGraphEdge>();
        var diagnostics = new List<CodeGraphDiagnostic>();
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Passo 1: cada arquivo vira um nó, para que a resolução do passo 2 encontre destinos.
        foreach (var file in files)
        {
            var relative = Relative(root, file);
            var nodeId = $"ts:{relative}";
            byPath[relative] = nodeId;
            nodes.Add(new CodeGraphNode(
                nodeId,
                Path.GetFileNameWithoutExtension(file),
                CodeGraphNodeKind.Type,
                relative,
                ModuleOf(relative)));
        }

        // Passo 2: os imports viram arestas "quem depende" → "de quem depende".
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Relative(root, file);
            var from = byPath[relative];
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException exception)
            {
                diagnostics.Add(new CodeGraphDiagnostic(
                    "codegraph.ts.unreadable",
                    CodeGraphDiagnosticSeverity.Warning,
                    exception.Message,
                    relative));
                continue;
            }

            foreach (Match match in ImportPattern().Matches(text))
            {
                var specifier = match.Groups["path"].Value;
                var target = Resolve(specifier, relative, byPath);

                // Import não resolvido é quase sempre pacote externo: não é defeito, e criar nó
                // para ele encheria o grafo de folhas que ninguém consulta.
                if (target is not null && !string.Equals(target, from, StringComparison.Ordinal))
                {
                    edges.Add(new CodeGraphEdge(from, target, CodeGraphEdgeKind.References));
                }
            }
        }

        var graph = CodeGraph.Create(
            nodes,
            edges.DistinctBy(edge => (edge.FromNodeId, edge.ToNodeId, edge.Kind)).ToArray());

        // SyntaxOnly é o escopo honesto: este índice lê imports, não tipa. Declarar Semantic aqui
        // faria o consumidor confiar num diagnóstico que ninguém produziu.
        return Task.FromResult(new CodeGraphBuildResult(
            graph, diagnostics, files.Length, CodeGraphDiagnosticScope.SyntaxOnly));
    }

    /// <summary>
    /// Resolve o especificador para um nó existente. Cobre o relativo (<c>./x</c>, <c>../x</c>) e o
    /// alias <c>@/</c> que este frontend usa para a raiz de <c>src</c>. Extensão é opcional no
    /// TypeScript, então cada candidato é testado com as extensões conhecidas e com <c>/index</c>.
    /// </summary>
    private static string? Resolve(
        string specifier,
        string fromRelative,
        Dictionary<string, string> byPath)
    {
        if (string.IsNullOrWhiteSpace(specifier))
        {
            return null;
        }

        string basePath;
        if (specifier.StartsWith("@/", StringComparison.Ordinal))
        {
            basePath = specifier[2..];
        }
        else if (specifier.StartsWith('.'))
        {
            var directory = Path.GetDirectoryName(fromRelative) ?? string.Empty;
            basePath = Normalize(Path.Combine(directory, specifier));
        }
        else
        {
            // Pacote externo: fora do escopo do índice do projeto.
            return null;
        }

        foreach (var candidate in Candidates(basePath))
        {
            if (byPath.TryGetValue(candidate, out var nodeId))
            {
                return nodeId;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string basePath)
    {
        yield return basePath;
        foreach (var extension in SourceExtensions)
        {
            yield return basePath + extension;
            yield return $"{basePath}/index{extension}";
        }
    }

    private static bool IsExcluded(string file, string root, HashSet<string> excluded) =>
        Relative(root, file)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(excluded.Contains);

    private static string Relative(string root, string file) =>
        Normalize(Path.GetRelativePath(root, file));

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Replace("/./", "/", StringComparison.Ordinal).TrimStart('/');

    /// <summary>O módulo é a feature: é a fronteira que o time usa para falar de impacto.</summary>
    private static string ModuleOf(string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var featureIndex = Array.FindIndex(
            segments, segment => string.Equals(segment, "features", StringComparison.Ordinal));

        return featureIndex >= 0 && featureIndex + 1 < segments.Length
            ? segments[featureIndex + 1]
            : segments.Length > 1 ? segments[0] : "root";
    }

    /// <summary>
    /// Captura `import … from '…'`, `export … from '…'` e `import('…')` dinâmico. Comentário e
    /// string solta ficam de fora porque o padrão exige a palavra-chave antes do especificador.
    /// </summary>
    [GeneratedRegex(
        @"(?:\bimport\b|\bexport\b)[^;'""]*?from\s*['""](?<path>[^'""]+)['""]|\bimport\s*\(\s*['""](?<path>[^'""]+)['""]\s*\)",
        RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex ImportPattern();
}
