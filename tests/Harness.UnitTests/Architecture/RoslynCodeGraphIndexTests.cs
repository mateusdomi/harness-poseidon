using Harness.Host.Architecture;
using Harness.SharedKernel.CodeGraph;

namespace Harness.UnitTests.Architecture;

/// <summary>
/// Derivação do grafo com Roslyn (B6/F15). O gate da fase pede que o índice RECONSTRUA DO ZERO — e é
/// isso que o primeiro teste prova, comparando o digest de duas derivações independentes da mesma
/// árvore.
///
/// O segundo teste é o que justifica ter posto um compilador aqui em vez de uma varredura de texto:
/// um nome citado dentro de string ou comentário não vira dependência, porque o modelo semântico sabe
/// a diferença entre mencionar e referenciar.
/// </summary>
public sealed class RoslynCodeGraphIndexTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("harness-codegraph-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Limpeza de temporário não é asserção do teste.
        }
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteProject(string relativePath) =>
        Write(relativePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

    private Task<CodeGraphBuildResult> BuildAsync() =>
        new RoslynCodeGraphIndex().BuildAsync(
            new CodeGraphBuildRequest("PROJ-1", _root.FullName));

    private void GivenTwoCoupledTypes()
    {
        WriteProject("src/App/App.csproj");
        Write("src/App/Contrato.cs", """
            namespace App;

            public interface IContrato
            {
                void Fazer();
            }
            """);
        Write("src/App/Consumidor.cs", """
            namespace App;

            public sealed class Consumidor
            {
                private readonly IContrato _contrato;

                public Consumidor(IContrato contrato) => _contrato = contrato;

                public void Executar() => _contrato.Fazer();
            }
            """);
    }

    [Fact]
    public async Task RebuildingFromScratchProducesTheSameGraph()
    {
        GivenTwoCoupledTypes();

        var first = await BuildAsync();
        var second = await BuildAsync();

        Assert.NotEmpty(first.Graph.Nodes);
        // Sem esta igualdade não existe a pergunta "o grafo mudou?", e sem ela o índice não serve
        // de gate: qualquer divergência poderia ser culpa da ordem de leitura dos arquivos.
        Assert.Equal(first.Graph.Digest, second.Graph.Digest);
        Assert.Equal(first.Graph.Nodes.Count, second.Graph.Nodes.Count);
        Assert.Equal(first.Graph.Edges.Count, second.Graph.Edges.Count);
        Assert.Equal(2, first.FilesIndexed);
    }

    [Fact]
    public async Task ARealReferenceBecomesAnEdgeAndAMereMentionDoesNot()
    {
        GivenTwoCoupledTypes();
        Write("src/App/Falante.cs", """
            namespace App;

            /// <summary>Fala de IContrato sem depender dele.</summary>
            public sealed class Falante
            {
                // IContrato aparece aqui só como texto.
                public string Nome => "IContrato";
            }
            """);

        var result = await BuildAsync();
        var contrato = Assert.Single(
            result.Graph.Nodes,
            node => node.Symbol.EndsWith("IContrato", StringComparison.Ordinal));
        var dependents = result.Graph.DependentsOf(contrato.NodeId);

        Assert.Contains(
            dependents,
            id => id.EndsWith("Consumidor", StringComparison.Ordinal));
        // É esta linha que separa grafo semântico de grep.
        Assert.DoesNotContain(
            dependents,
            id => id.EndsWith("Falante", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InheritanceIsRecordedAsInheritance()
    {
        GivenTwoCoupledTypes();
        Write("src/App/Implementacao.cs", """
            namespace App;

            public sealed class Implementacao : IContrato
            {
                public void Fazer()
                {
                }
            }
            """);

        var result = await BuildAsync();

        Assert.Contains(
            result.Graph.Edges,
            edge => edge.Kind == CodeGraphEdgeKind.Inherits &&
                edge.FromNodeId.EndsWith("Implementacao", StringComparison.Ordinal) &&
                edge.ToNodeId.EndsWith("IContrato", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheModuleOfAFileIsTheProjectThatCompilesIt()
    {
        GivenTwoCoupledTypes();

        var result = await BuildAsync();

        Assert.All(
            result.Graph.Nodes.Where(node => node.Kind == CodeGraphNodeKind.Type),
            node => Assert.Equal("App", node.Module));
    }

    [Fact]
    public async Task ModuleDependenciesAreDerivedInsteadOfDeclared()
    {
        WriteProject("src/Nucleo/Nucleo.csproj");
        Write("src/Nucleo/Base.cs", """
            namespace Nucleo;

            public sealed class Base
            {
                public int Valor => 1;
            }
            """);
        WriteProject("src/Borda/Borda.csproj");
        Write("src/Borda/Uso.cs", """
            namespace Borda;

            public sealed class Uso
            {
                public int Ler(Nucleo.Base baseDoNucleo) => baseDoNucleo.Valor;
            }
            """);

        var result = await BuildAsync();
        var dependencies = result.Graph.DeriveModuleDependencies();

        // O self-map deixa de ser afirmação de alguém e passa a sair das referências que existem.
        Assert.Contains(dependencies, item => item.FromModule == "Borda" && item.ToModule == "Nucleo");
        Assert.DoesNotContain(dependencies, item => item.FromModule == "Nucleo");
    }

    [Fact]
    public async Task GeneratedDirectoriesAreNotIndexed()
    {
        GivenTwoCoupledTypes();
        Write("src/App/obj/Release/Gerado.cs", """
            namespace App;

            public sealed class Gerado
            {
            }
            """);

        var result = await BuildAsync();

        // Indexar obj/ duplicaria tipos do build e envenenaria a contagem de dependentes.
        Assert.Equal(2, result.FilesIndexed);
        Assert.DoesNotContain(
            result.Graph.Nodes,
            node => node.Symbol.EndsWith("Gerado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileThatDoesNotParseIsADefinitiveDiagnostic()
    {
        GivenTwoCoupledTypes();
        Write("src/App/Quebrado.cs", "namespace App; public sealed class Quebrado { void M( }");

        var result = await BuildAsync();

        Assert.False(result.Compiles);
        // Um arquivo mal formado costuma render mais de um erro; o que importa é ele ser apontado
        // com arquivo e linha, para o gate poder dizer ONDE.
        Assert.Contains(
            result.Errors,
            item => item.FilePath == "src/App/Quebrado.cs" &&
                item.Severity == CodeGraphDiagnosticSeverity.Error &&
                item.Line is not null);
        // O alcance é declarado: arquivo que não parseia não compila em build nenhum, e isso é o
        // único tipo de reprovação que esta passada tem autoridade para afirmar.
        Assert.Equal(CodeGraphDiagnosticScope.SyntaxOnly, result.DiagnosticScope);
    }

    [Fact]
    public async Task HealthySourcesProduceNoDiagnostics()
    {
        GivenTwoCoupledTypes();

        var result = await BuildAsync();

        Assert.True(result.Compiles);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AnIndexingRootThatDoesNotExistFailsLoudly() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new RoslynCodeGraphIndex().BuildAsync(
                new CodeGraphBuildRequest("PROJ-1", Path.Combine(_root.FullName, "inexistente"))));
}
