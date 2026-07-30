using System.Text.RegularExpressions;

namespace Harness.ArchitectureTests;

/// <summary>
/// Política escrita e testada não é política ativa.
///
/// A campanha multiagente entregou várias políticas como <c>public static class X { Evaluate(…) }</c>
/// com bateria de testes verde e <b>nenhum ponto de chamada</b> no produto. O gate de cada fase era
/// "build + testes verdes", que não distingue "roda" de "existe" — então a fase podia ser declarada
/// concluída sem que uma linha dela executasse.
///
/// Este teste fecha esse buraco. O critério é deliberadamente estreito para não produzir falso
/// positivo: só classes <b>estáticas</b> entram, porque elas não podem ser injetadas por DI — se
/// ninguém as nomeia, ninguém as executa. Serviços instanciáveis ficam de fora, já que o registro
/// no contêiner é uma referência legítima que este teste não tenta interpretar.
///
/// <see cref="DeclaredDebt"/> segue o mesmo padrão das exceções do gate de vocabulário do frontend:
/// cada linha é dívida declarada com dono, não perdão permanente. Ao ligar a política, remova a
/// linha; ao removê-la do repositório, idem. Acrescentar linha nova exige justificar por que uma
/// política nasceu sem consumidor.
/// </summary>
public sealed class PolicyWiringTests
{
    /// <summary>
    /// Políticas hoje sem consumidor de produção, com a fase que as deixou assim.
    /// Auditoria de 30/07/2026 (RELATORIO-VALIDACAO-POSEIDON.md, DEF-02 a DEF-05, DEF-07).
    /// </summary>
    private static readonly Dictionary<string, string> DeclaredDebt = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void EveryStaticPolicyIsCalledByProductionCodeOrDeclaredAsDebt()
    {
        var root = RepositoryRoot();
        var sources = ProductionSources(root);
        var orphans = new List<string>();

        foreach (var file in ApplicationPolicyFiles(root))
        {
            foreach (var type in StaticTypesDeclaredIn(file))
            {
                if (IsReferencedOutside(type, file, sources))
                {
                    continue;
                }

                if (DeclaredDebt.ContainsKey(type))
                {
                    continue;
                }

                orphans.Add($"{type} ({Path.GetRelativePath(root, file)})");
            }
        }

        Assert.True(
            orphans.Count == 0,
            "Política estática sem nenhum consumidor de produção — ela nunca executa, por mais "
                + "testada que esteja. Ligue-a ao fluxo, remova-a, ou declare a dívida em "
                + $"{nameof(DeclaredDebt)} com a fase responsável:{Environment.NewLine}  "
                + string.Join($"{Environment.NewLine}  ", orphans));
    }

    /// <summary>
    /// Dívida quitada não pode continuar declarada: uma linha obsoleta mente sobre a cobertura
    /// do gate exatamente como a classificação desatualizada mente no gate de vocabulário.
    /// </summary>
    [Fact]
    public void DeclaredDebtHasNoStaleEntries()
    {
        var root = RepositoryRoot();
        var sources = ProductionSources(root);
        var files = ApplicationPolicyFiles(root).ToArray();
        var stale = new List<string>();

        foreach (var (type, reason) in DeclaredDebt)
        {
            var declaringFile = files.FirstOrDefault(file => StaticTypesDeclaredIn(file).Contains(type));

            if (declaringFile is null)
            {
                stale.Add($"{type} — não existe mais como política estática ({reason})");
                continue;
            }

            if (IsReferencedOutside(type, declaringFile, sources))
            {
                stale.Add($"{type} — já tem consumidor de produção; remova a linha ({reason})");
            }
        }

        Assert.True(
            stale.Count == 0,
            $"Entradas obsoletas em {nameof(DeclaredDebt)}:{Environment.NewLine}  "
                + string.Join($"{Environment.NewLine}  ", stale));
    }

    private static IEnumerable<string> ApplicationPolicyFiles(string root)
    {
        var modules = Path.Combine(root, "src", "Modules");

        return Directory
            .EnumerateDirectories(modules)
            .Select(module => Path.Combine(module, "Application"))
            .Where(Directory.Exists)
            .SelectMany(application => Directory.EnumerateFiles(application, "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsGenerated(file))
            .OrderBy(file => file, StringComparer.Ordinal);
    }

    /// <summary>Todo `.cs` de produção — o universo onde uma chamada poderia existir.</summary>
    private static (string Path, string Text)[] ProductionSources(string root)
    {
        return Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsGenerated(file))
            .Select(file => (Path: file, Text: File.ReadAllText(file)))
            .ToArray();
    }

    private static bool IsGenerated(string file)
    {
        var normalized = file.Replace('\\', '/');

        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal)
            || normalized.Contains("/wwwroot/", StringComparison.Ordinal);
    }

    private static HashSet<string> StaticTypesDeclaredIn(string file)
    {
        var matches = Regex.Matches(
            File.ReadAllText(file),
            @"^public\s+static\s+class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline);

        return matches.Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Uma referência conta quando o nome aparece em outro arquivo de produção fora de comentário.
    /// Comentários são descartados de propósito: <c>DispatchAuthorityPolicy</c> é citada apenas
    /// dentro de um <c>&lt;see&gt;</c> e isso não a faz executar.
    /// </summary>
    private static bool IsReferencedOutside(
        string type,
        string declaringFile,
        (string Path, string Text)[] sources)
    {
        var pattern = new Regex($@"\b{Regex.Escape(type)}\b");

        foreach (var (path, text) in sources)
        {
            if (string.Equals(path, declaringFile, StringComparison.Ordinal))
            {
                continue;
            }

            if (pattern.IsMatch(WithoutComments(text)))
            {
                return true;
            }
        }

        return false;
    }

    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Não foi possível localizar a raiz contendo Harness.sln.");
    }
}
