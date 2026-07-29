using Harness.SharedKernel.CodeGraph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Harness.Host.Architecture;

/// <summary>
/// Índice de grafo de código para C#, derivado com o Roslyn como biblioteca (B6/F15).
///
/// <b>Por que compilação em memória e não MSBuildWorkspace.</b> O workspace carregaria os csproj de
/// verdade — e exigiria o MSBuild em tempo de execução, que a fronteira da fase proíbe. Parsear o
/// conjunto de fontes e montar uma <see cref="CSharpCompilation"/> dá o modelo SEMÂNTICO (que é o que
/// distingue este índice de uma varredura de texto: <c>Foo</c> aqui resolve para o símbolo certo, não
/// para toda linha que contém a palavra "Foo") sem esse custo nem esse acoplamento.
///
/// <b>O que essa escolha custa, dito na cara.</b> A compilação montada aqui é sintética: um único
/// conjunto de fontes, com as referências da plataforma, sem as fronteiras de projeto e sem os
/// pacotes de cada csproj. Ela serve perfeitamente para descobrir quem referencia quem DENTRO do
/// código indexado, e NÃO serve para afirmar que o build está quebrado. Por isso os diagnósticos
/// publicados são de <see cref="CodeGraphDiagnosticScope.SyntaxOnly"/>: arquivo que não parseia não
/// compila em build nenhum, e essa afirmação é definitiva. Erro semântico dessa passada poderia ser
/// apenas referência ausente — reprovar card com base nele seria inventar reprovação.
///
/// <b>Determinismo.</b> Os arquivos são ordenados por caminho relativo (ordinal) antes de parsear, e
/// todo o resto do grafo é ordenado em <see cref="CodeGraph.Create"/>. Duas derivações da mesma
/// árvore produzem o mesmo <see cref="CodeGraph.Digest"/> — que é o que o gate desta fase cobra.
/// </summary>
public sealed class RoslynCodeGraphIndex : ICodeGraphIndex
{
    /// <summary>
    /// Diretórios que nunca são código-fonte do projeto. Indexar <c>obj/</c> duplicaria cada tipo
    /// (os gerados do build) e envenenaria a contagem de dependentes.
    /// </summary>
    private static readonly string[] AlwaysExcludedSegments =
    [
        "obj", "bin", "node_modules", ".git", ".artifacts", ".tooling", ".vs"
    ];

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None);

    public string Language => "csharp";

    public async Task<CodeGraphBuildResult> BuildAsync(
        CodeGraphBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);

        var root = Path.GetFullPath(request.RootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Raiz de indexação inexistente: {root}");
        }

        var excluded = AlwaysExcludedSegments
            .Concat(request.ExcludedDirectorySegments ?? [])
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sourceFiles = EnumerateSourceFiles(root, request.IncludedDirectories, excluded);
        var moduleIndex = BuildModuleIndex(root, excluded);

        var diagnostics = new List<CodeGraphDiagnostic>();
        var trees = new List<(SyntaxTree Tree, string RelativePath, string Module)>();
        foreach (var (absolutePath, relativePath) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(absolutePath, cancellationToken)
                .ConfigureAwait(false);
            var tree = CSharpSyntaxTree.ParseText(
                SourceText.From(text),
                ParseOptions,
                path: relativePath,
                cancellationToken: cancellationToken);

            foreach (var diagnostic in tree.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }

                var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                diagnostics.Add(new CodeGraphDiagnostic(
                    diagnostic.Id,
                    CodeGraphDiagnosticSeverity.Error,
                    diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                    relativePath,
                    line));
            }

            trees.Add((tree, relativePath, ResolveModule(moduleIndex, relativePath)));
        }

        var compilation = CSharpCompilation.Create(
            "Harness.CodeGraph",
            trees.Select(item => item.Tree),
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var nodes = new List<CodeGraphNode>();
        var edges = new List<CodeGraphEdge>();
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);

        // Passada 1 — declarações. Todo nó tem de existir antes de qualquer aresta, senão uma aresta
        // para um tipo que só aparece depois seria descartada por "sair do índice".
        foreach (var (tree, relativePath, module) in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var rootNode = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            foreach (var declaration in rootNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration, cancellationToken)
                    is not INamedTypeSymbol symbol)
                {
                    continue;
                }

                var nodeId = NodeIdOf(symbol);
                if (nodeId is null)
                {
                    continue;
                }

                declared[nodeId] = nodeId;
                nodes.Add(new CodeGraphNode(
                    nodeId,
                    symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    CodeGraphNodeKind.Type,
                    relativePath,
                    module));
            }
        }

        foreach (var module in trees
                     .Select(item => item.Module)
                     .Where(module => module.Length > 0)
                     .Distinct(StringComparer.Ordinal))
        {
            nodes.Add(new CodeGraphNode(
                "M:" + module, module, CodeGraphNodeKind.Module, string.Empty, module));
        }

        // Passada 2 — referências. Só arestas cujos DOIS lados foram indexados.
        foreach (var (tree, _, module) in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var rootNode = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var ownerCache = new Dictionary<SyntaxNode, string?>();

            foreach (var declaration in rootNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration, cancellationToken)
                    is not INamedTypeSymbol symbol)
                {
                    continue;
                }

                var ownerId = NodeIdOf(symbol);
                if (ownerId is null)
                {
                    continue;
                }

                ownerCache[declaration] = ownerId;
                edges.Add(new CodeGraphEdge("M:" + module, ownerId, CodeGraphEdgeKind.Contains));

                foreach (var baseType in BaseTypesOf(symbol))
                {
                    var baseId = NodeIdOf(baseType);
                    if (baseId is not null && declared.ContainsKey(baseId))
                    {
                        edges.Add(new CodeGraphEdge(ownerId, baseId, CodeGraphEdgeKind.Inherits));
                    }
                }
            }

            foreach (var name in rootNode.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owner = EnclosingTypeId(name, model, ownerCache, cancellationToken);
                if (owner is null)
                {
                    continue;
                }

                var referenced = ReferencedTypeOf(model, name, cancellationToken);
                if (referenced is null)
                {
                    continue;
                }

                var referencedId = NodeIdOf(referenced);
                if (referencedId is null ||
                    !declared.ContainsKey(referencedId) ||
                    string.Equals(referencedId, owner, StringComparison.Ordinal))
                {
                    continue;
                }

                edges.Add(new CodeGraphEdge(owner, referencedId, CodeGraphEdgeKind.References));
            }
        }

        return new CodeGraphBuildResult(
            CodeGraph.Create(nodes, edges),
            [.. diagnostics
                .OrderBy(diagnostic => diagnostic.FilePath, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Line)
                .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)],
            trees.Count,
            CodeGraphDiagnosticScope.SyntaxOnly);
    }

    /// <summary>
    /// Identidade do tipo: o id de documentação da DEFINIÇÃO original (<c>T:Ns.Tipo`1</c>). É estável
    /// entre reconstruções e colapsa <c>List&lt;Foo&gt;</c> e <c>List&lt;Bar&gt;</c> no mesmo nó — o
    /// que é o correto, porque quem quebra ao mudar o tipo é a definição, não cada instanciação.
    /// </summary>
    private static string? NodeIdOf(INamedTypeSymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        if (definition.TypeKind == TypeKind.Error || definition.Locations.Length == 0)
        {
            return null;
        }

        var id = definition.GetDocumentationCommentId();
        return string.IsNullOrWhiteSpace(id) || id.Contains('!', StringComparison.Ordinal) ? null : id;
    }

    private static IEnumerable<INamedTypeSymbol> BaseTypesOf(INamedTypeSymbol symbol)
    {
        if (symbol.BaseType is { SpecialType: SpecialType.None } baseType)
        {
            yield return baseType;
        }

        foreach (var contract in symbol.Interfaces)
        {
            yield return contract;
        }
    }

    /// <summary>
    /// O tipo que este nome referencia. Quando o nome é de um MEMBRO (método, propriedade, campo), o
    /// que importa para impacto é o tipo que o declara: chamar <c>Servico.Fazer()</c> cria dependência
    /// de <c>Servico</c>.
    /// </summary>
    private static INamedTypeSymbol? ReferencedTypeOf(
        SemanticModel model,
        SimpleNameSyntax name,
        CancellationToken cancellationToken)
    {
        var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
        return symbol switch
        {
            INamedTypeSymbol type => type,
            null => null,
            _ => symbol.ContainingType
        };
    }

    private static string? EnclosingTypeId(
        SyntaxNode node,
        SemanticModel model,
        Dictionary<SyntaxNode, string?> cache,
        CancellationToken cancellationToken)
    {
        var declaration = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (declaration is null)
        {
            return null;
        }

        if (cache.TryGetValue(declaration, out var cached))
        {
            return cached;
        }

        var resolved = model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol
            ? NodeIdOf(symbol)
            : null;
        cache[declaration] = resolved;
        return resolved;
    }

    private static List<(string AbsolutePath, string RelativePath)> EnumerateSourceFiles(
        string root,
        IReadOnlyList<string>? includedDirectories,
        HashSet<string> excludedSegments)
    {
        var searchRoots = includedDirectories is { Count: > 0 }
            ? includedDirectories
                .Select(directory => Path.GetFullPath(Path.Combine(root, directory)))
                .Where(Directory.Exists)
                .ToArray()
            : [root];

        var found = new List<(string AbsolutePath, string RelativePath)>();
        foreach (var searchRoot in searchRoots)
        {
            foreach (var path in Directory.EnumerateFiles(searchRoot, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Relative(root, path);
                if (IsExcluded(relative, excludedSegments))
                {
                    continue;
                }

                found.Add((path, relative));
            }
        }

        return [.. found
            .DistinctBy(item => item.RelativePath, StringComparer.Ordinal)
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Mapa diretório → módulo, derivado dos csproj REAIS: o módulo de um arquivo é o projeto que o
    /// compila, não uma convenção de nome que alguém precise manter.
    /// </summary>
    private static List<(string Directory, string Module)> BuildModuleIndex(
        string root,
        HashSet<string> excludedSegments)
    {
        var projects = new List<(string Directory, string Module)>();
        foreach (var path in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Relative(root, path);
            if (IsExcluded(relative, excludedSegments))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(relative) ?? string.Empty;
            projects.Add((
                directory.Length == 0 ? string.Empty : directory + "/",
                Path.GetFileNameWithoutExtension(relative)));
        }

        // Do mais específico para o menos: um arquivo dentro de src/Modules/X pertence a X, e não ao
        // projeto de um diretório acima que também o contenha.
        return [.. projects.OrderByDescending(item => item.Directory.Length)];
    }

    private static string ResolveModule(
        List<(string Directory, string Module)> moduleIndex,
        string relativePath)
    {
        foreach (var (directory, module) in moduleIndex)
        {
            if (directory.Length == 0 ||
                relativePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                return module;
            }
        }

        return string.Empty;
    }

    private static bool IsExcluded(string relativePath, HashSet<string> excludedSegments) =>
        relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Any(excludedSegments.Contains);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static IReadOnlyList<MetadataReference> PlatformReferences()
    {
        // As referências da plataforma vêm do próprio runtime que está executando — nada de baixar,
        // nada de caminho fixo de SDK. Sem elas, expressões que passam pela BCL não ligam e o modelo
        // semântico perde arestas reais entre tipos nossos.
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return [.. trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }
}
