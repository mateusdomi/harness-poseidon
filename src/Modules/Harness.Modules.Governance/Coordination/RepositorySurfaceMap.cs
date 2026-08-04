using System.Globalization;

namespace Harness.Modules.Governance.Coordination;

/// <summary>
/// Uma SUPERFÍCIE real do repositório: um nome pelo qual as pessoas se referem a ela e os paths
/// que ela de fato ocupa em disco.
/// </summary>
public sealed record RepositorySurface(
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Paths);

/// <summary>
/// O mapa das superfícies REAIS do repositório de um projeto — módulos, projetos de teste,
/// features de frontend, migrations, infraestrutura.
///
/// Existe porque o escopo de um card estava sendo derivado do PAPEL, não do trabalho: todo card de
/// backend reivindicava `src/**` inteiro, então dois cards independentes do mesmo projeto nunca
/// rodavam juntos — o paralelismo escalava por papéis, não por trabalho, e a promessa de dezenas
/// de agentes simultâneos era estruturalmente impossível.
///
/// O mapa é construído por LEITURA DO DISCO, não por convenção adivinhada: um repositório que não
/// tenha a estrutura esperada simplesmente devolve menos superfícies, e o planejador cai no escopo
/// do papel — que é o comportamento anterior, seguro por construção.
/// </summary>
public sealed class RepositorySurfaceMap
{
    private readonly List<RepositorySurface> _surfaces;

    private RepositorySurfaceMap(List<RepositorySurface> surfaces) => _surfaces = surfaces;

    public IReadOnlyList<RepositorySurface> Surfaces => _surfaces;

    /// <summary>Mapa vazio: nenhum estreitamento possível, o papel decide.</summary>
    public static RepositorySurfaceMap Empty { get; } = new([]);

    /// <summary>
    /// Costura de teste: monta o mapa a partir de superfícies declaradas, sem depender da árvore
    /// de um repositório real. Existe porque a regra que importa — o recorte nunca tira do card o
    /// lugar onde ele foi mandado escrever — precisa ser exercitada com superfícies controladas,
    /// e derivá-las de diretórios de mentira testaria a varredura em vez da regra.
    /// </summary>
    internal static RepositorySurfaceMap ForSurfaces(IEnumerable<RepositorySurface> surfaces) =>
        new([.. surfaces]);

    /// <summary>
    /// Lê a estrutura real da raiz do repositório. Nunca lança: um caminho inacessível devolve o
    /// mapa vazio, porque falhar aqui derrubaria o despacho por um detalhe de ambiente.
    /// </summary>
    public static RepositorySurfaceMap Build(string repositoryRoot)
    {
        var surfaces = new List<RepositorySurface>();
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            {
                return Empty;
            }

            AddModules(surfaces, repositoryRoot);
            AddFrontendFeatures(surfaces, repositoryRoot);
            AddMigrations(surfaces, repositoryRoot);
            AddInfrastructure(surfaces, repositoryRoot);
        }
        catch (IOException)
        {
            return Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return Empty;
        }

        return new RepositorySurfaceMap(surfaces);
    }

    /// <summary>
    /// Superfícies mencionadas no texto do card, da mais específica para a menos. A ordem importa:
    /// citar "Harness.Modules.Agents" deve pesar mais do que citar "agents" de passagem.
    /// </summary>
    public IReadOnlyList<RepositorySurface> Match(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _surfaces.Count == 0)
        {
            return [];
        }

        var haystack = Normalize(text);
        return
        [
            .. _surfaces
                .Select(surface => (surface, score: Score(surface, haystack)))
                .Where(entry => entry.score > 0)
                .OrderByDescending(entry => entry.score)
                .ThenBy(entry => entry.surface.Name, StringComparer.Ordinal)
                .Select(entry => entry.surface),
        ];
    }

    /// <summary>
    /// Reduz o texto a tokens separados por espaço. Sem isto, "em Harness.Modules.Agents." não
    /// casava com o módulo por causa do ponto final — e o card voltava ao escopo do papel por um
    /// detalhe de pontuação, que é o pior tipo de falha: silenciosa e plausível.
    ///
    /// O ponto NÃO é separador dentro do nome do módulo, então ele é preservado no meio da palavra
    /// e removido apenas nas bordas.
    /// </summary>
    private static string Normalize(string text)
    {
        var lowered = text.ToLower(CultureInfo.InvariantCulture);
        var tokens = lowered
            .Split(
                [' ', '\n', '\r', '\t', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '`', '/', '\\', '*'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('.', '-', '!', '?'))
            .Where(token => token.Length > 0);
        return $" {string.Join(' ', tokens)} ";
    }

    /// <summary>
    /// Pontuação do casamento: o alias mais LONGO encontrado vence. Um alias curto casado por
    /// acaso ("tools", "board") não pode ter o mesmo peso do nome completo do módulo.
    /// </summary>
    private static int Score(RepositorySurface surface, string haystack) =>
        surface.Aliases
            .Where(alias => haystack.Contains(
                $" {alias.ToLower(CultureInfo.InvariantCulture)} ", StringComparison.Ordinal))
            .Select(alias => alias.Length)
            .DefaultIfEmpty(0)
            .Max();

    private static void AddModules(List<RepositorySurface> surfaces, string root)
    {
        var modulesRoot = Path.Combine(root, "src", "Modules");
        if (!Directory.Exists(modulesRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(modulesRoot))
        {
            var name = Path.GetFileName(directory);
            // Sufixo curto do módulo ("Agents" de "Harness.Modules.Agents"): é assim que as
            // pessoas — e a chefe — se referem a ele na conversa.
            var shortName = name.Split('.').LastOrDefault() ?? name;
            var paths = new List<string> { $"src/Modules/{name}/**" };

            // O projeto de teste correspondente entra no MESMO claim: alterar um módulo sem poder
            // tocar no teste dele produziria card que não consegue provar o próprio trabalho.
            foreach (var testProject in new[] { "Harness.UnitTests", "Harness.IntegrationTests" })
            {
                if (Directory.Exists(Path.Combine(root, "tests", testProject, shortName)))
                {
                    paths.Add($"tests/{testProject}/{shortName}/**");
                }
            }

            surfaces.Add(new RepositorySurface(name, [name, shortName], paths));
        }
    }

    private static void AddFrontendFeatures(List<RepositorySurface> surfaces, string root)
    {
        var featuresRoot = Path.Combine(root, "frontend", "src", "features");
        if (!Directory.Exists(featuresRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(featuresRoot))
        {
            var name = Path.GetFileName(directory);
            var paths = new List<string> { $"frontend/src/features/{name}/**" };
            if (Directory.Exists(Path.Combine(root, "frontend", "src", "services", name)))
            {
                paths.Add($"frontend/src/services/{name}/**");
            }

            surfaces.Add(new RepositorySurface($"frontend:{name}", [name], paths));
        }
    }

    private static void AddMigrations(List<RepositorySurface> surfaces, string root)
    {
        var paths = new List<string>();
        foreach (var provider in new[] { "Harness.Persistence.Sqlite", "Harness.Persistence.Postgres" })
        {
            if (Directory.Exists(Path.Combine(root, "src", provider, "Migrations")))
            {
                paths.Add($"src/{provider}/Migrations/**");
            }
        }

        if (paths.Count > 0)
        {
            // Migration é sempre DUAL neste repositório: quem mexe em uma precisa da outra, senão
            // o card nasce condenado a quebrar a paridade entre providers.
            surfaces.Add(new RepositorySurface(
                "migrations", ["migration", "migrations", "migração", "migracao"], paths));
        }
    }

    private static void AddInfrastructure(List<RepositorySurface> surfaces, string root)
    {
        if (Directory.Exists(Path.Combine(root, "infra")))
        {
            surfaces.Add(new RepositorySurface(
                "infra", ["infra", "infraestrutura", "infrastructure"], ["infra/**"]));
        }

        if (Directory.Exists(Path.Combine(root, "tools", "backend")))
        {
            surfaces.Add(new RepositorySurface(
                "tools", ["tooling", "ferramental", "scripts"], ["tools/backend/**"]));
        }
    }
}
