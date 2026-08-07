using System.Text.RegularExpressions;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Constata, olhando a árvore entregue, os fatos de EXISTÊNCIA E FORMA que o perfil efetivo exige.
///
/// Este é o mecanismo que responde à pergunta que originou a Fase 3: se o Poseidon receber
/// "crie um sistema de empréstimos" e produzir só uma API, existe algo — independente do modelo —
/// capaz de perceber que o frontend obrigatório não está lá? Aqui está.
///
/// O que ele NÃO faz, de propósito: dizer que algo funciona. Funcionamento exige execução
/// controlada, e o gate cobra <see cref="ProductEvidenceProvenance.Verified"/> para isso. Uma
/// pasta chamada `frontend` não prova frontend; um `package.json` que declara o framework do
/// perfil e um ponto de entrada, sim — prova que existe, não que builda.
/// </summary>
public sealed class RepositoryEvidenceCollector(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<ProductEvidence> Collect(
        ProjectEffectiveProfile profile,
        IProductWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(workspace);

        var now = _time.GetUtcNow();
        var evidence = new List<ProductEvidence>();

        if (profile.Backend.Required)
        {
            evidence.Add(InspectBackend(profile, workspace, now));
        }

        if (profile.Frontend.Required)
        {
            evidence.Add(InspectFrontend(profile, workspace, now));
        }

        if (profile.Api.Required)
        {
            evidence.Add(InspectApi(workspace, now));
        }

        if (profile.Data.Required)
        {
            evidence.Add(InspectMigrations(workspace, now));

            if (profile.Data.Database is { Length: > 0 })
            {
                evidence.Add(InspectDataAccess(profile, workspace, now));
            }
        }

        evidence.Add(InspectRunbook(workspace, now));
        return evidence;
    }

    /// <summary>
    /// Backend EXISTE quando há projeto e ele declara o runtime do perfil. Um `.csproj` com outro
    /// target framework não é o backend que a arquitetura decidiu — é outro backend. Que ele
    /// COMPILE é outro fato, e olhar não prova.
    /// </summary>
    private static ProductEvidence InspectBackend(
        ProjectEffectiveProfile profile, IProductWorkspace workspace, DateTimeOffset now)
    {
        var projects = workspace.Find("*.csproj");
        if (projects.Count == 0)
        {
            return Observed(
                ProductEvidenceKind.BackendPresent, false, workspace, now,
                "Nenhum projeto .csproj encontrado na árvore entregue.");
        }

        var expected = ExpectedTargetFramework(profile.Backend.Runtime);
        if (expected is null)
        {
            return Observed(
                ProductEvidenceKind.BackendPresent, true, workspace, now,
                $"{projects.Count} projeto(s) encontrados; o perfil não fixa runtime a conferir.",
                projects[0]);
        }

        var matching = projects.FirstOrDefault(path =>
            (workspace.ReadText(path) ?? string.Empty).Contains(expected, StringComparison.OrdinalIgnoreCase));

        return matching is not null
            ? Observed(
                ProductEvidenceKind.BackendPresent, true, workspace, now,
                $"Projeto declara {expected}, como o perfil exige.", matching)
            : Observed(
                ProductEvidenceKind.BackendPresent, false, workspace, now,
                $"Nenhum dos {projects.Count} projeto(s) declara {expected}, exigido pelo perfil " +
                $"({profile.Backend.Runtime}).");
    }

    /// <summary>
    /// Frontend existe quando há manifesto de pacote declarando o framework DO PERFIL e um ponto de
    /// entrada. O framework vem do perfil efetivo, nunca de um literal: um projeto que sobrescreveu
    /// React por Angular não pode reprovar por não ter React.
    /// </summary>
    private static ProductEvidence InspectFrontend(
        ProjectEffectiveProfile profile, IProductWorkspace workspace, DateTimeOffset now)
    {
        var manifests = workspace.Find("package.json")
            .Where(path => !path.Contains("node_modules/", StringComparison.Ordinal))
            .ToArray();

        if (manifests.Length == 0)
        {
            return Observed(
                ProductEvidenceKind.FrontendPresent, false, workspace, now,
                "O perfil exige interface e nenhum manifesto de pacote foi encontrado na entrega.");
        }

        var expected = profile.Frontend.Framework;
        foreach (var manifest in manifests)
        {
            var content = workspace.ReadText(manifest);
            if (content is null)
            {
                continue;
            }

            var declaresFramework = expected is not { Length: > 0 } ||
                DeclaresDependency(content, expected);
            if (!declaresFramework)
            {
                continue;
            }

            var directory = manifest.Contains('/', StringComparison.Ordinal)
                ? manifest[..manifest.LastIndexOf('/')]
                : string.Empty;
            var entryPoint = FindEntryPoint(workspace, directory);
            if (entryPoint is null)
            {
                return Observed(
                    ProductEvidenceKind.FrontendPresent, false, workspace, now,
                    $"{manifest} declara {expected}, mas nenhum ponto de entrada foi encontrado — " +
                    "manifesto sem aplicação não é interface.", manifest);
            }

            return Observed(
                ProductEvidenceKind.FrontendPresent, true, workspace, now,
                $"{manifest} declara {expected ?? "framework de UI"} e o ponto de entrada é {entryPoint}.",
                manifest);
        }

        return Observed(
            ProductEvidenceKind.FrontendPresent, false, workspace, now,
            $"Encontrado(s) {manifests.Length} manifesto(s), nenhum declarando {expected}, " +
            "que é o framework efetivo do projeto.");
    }

    /// <summary>API existe quando há superfície HTTP declarada em código, não uma pasta chamada api.</summary>
    private static ProductEvidence InspectApi(IProductWorkspace workspace, DateTimeOffset now)
    {
        foreach (var source in workspace.Find("*.cs", limit: 400))
        {
            var content = workspace.ReadText(source);
            if (content is null)
            {
                continue;
            }

            if (HttpSurface.IsMatch(content))
            {
                return Observed(
                    ProductEvidenceKind.ApiPresent, true, workspace, now,
                    $"Superfície HTTP declarada em {source}.", source);
            }
        }

        return Observed(
            ProductEvidenceKind.ApiPresent, false, workspace, now,
            "Nenhum endpoint HTTP encontrado na árvore entregue.");
    }

    private static ProductEvidence InspectMigrations(IProductWorkspace workspace, DateTimeOffset now)
    {
        var sql = workspace.Find("*.sql").Where(IsMigrationPath).ToArray();
        var efCore = workspace.Find("*.Designer.cs");
        var count = sql.Length + efCore.Count;
        var artifact = sql.Length > 0 ? sql[0] : efCore.Count > 0 ? efCore[0] : null;

        return count > 0
            ? Observed(
                ProductEvidenceKind.DatabaseMigrationValidated, true, workspace, now,
                $"{count} arquivo(s) de migration versionada encontrados.",
                artifact)
            : Observed(
                ProductEvidenceKind.DatabaseMigrationValidated, false, workspace, now,
                "O perfil exige persistência e nenhuma migration versionada foi encontrada.");
    }

    /// <summary>
    /// Acesso ao banco DO PERFIL existe quando um driver conhecido dele está DECLARADO como
    /// dependência (PackageReference no .csproj ou dependência no package.json). Uma pasta
    /// `oracle/` cheia de adaptadores sem driver não acessa banco nenhum — foi exatamente o
    /// caso Indicadores: migrations e mapeadores presentes, runtime persistindo em memória.
    /// Banco que o catálogo não conhece não reprova (nulo significa desconhecido).
    /// </summary>
    private static ProductEvidence InspectDataAccess(
        ProjectEffectiveProfile profile, IProductWorkspace workspace, DateTimeOffset now)
    {
        var database = profile.Data.Database!.Trim();
        var drivers = KnownDrivers(database);
        if (drivers.Length == 0)
        {
            return Observed(
                ProductEvidenceKind.DataAccessDeclared, true, workspace, now,
                $"O perfil fixa '{database}', banco fora do catálogo de drivers conhecidos; " +
                "sem prova de divergência, a constatação não reprova.");
        }

        foreach (var project in workspace.Find("*.csproj"))
        {
            var content = workspace.ReadText(project);
            if (content is not null && drivers.Any(driver =>
                    Regex.IsMatch(
                        content,
                        $"Include\\s*=\\s*\"{Regex.Escape(driver)}",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1))))
            {
                return Observed(
                    ProductEvidenceKind.DataAccessDeclared, true, workspace, now,
                    $"{project} declara driver de {database} como PackageReference.", project);
            }
        }

        foreach (var manifest in workspace.Find("package.json")
                     .Where(path => !path.Contains("node_modules/", StringComparison.Ordinal)))
        {
            var content = workspace.ReadText(manifest);
            if (content is not null && drivers.Any(driver => DeclaresDependency(content, driver)))
            {
                return Observed(
                    ProductEvidenceKind.DataAccessDeclared, true, workspace, now,
                    $"{manifest} declara driver de {database} como dependência.", manifest);
            }
        }

        return Observed(
            ProductEvidenceKind.DataAccessDeclared, false, workspace, now,
            $"O perfil exige {database} e nenhum manifesto da entrega declara um driver " +
            $"conhecido ({string.Join(", ", drivers)}). Adaptador sem driver é persistência de fachada.");
    }

    /// <summary>Drivers aceitos por banco, .NET e Node — nomes reais de pacote, nunca inventados.</summary>
    private static string[] KnownDrivers(string database) => database.Trim().ToLowerInvariant() switch
    {
        "oracle" or "oracle 19c" or "oracle19c" =>
            ["Oracle.ManagedDataAccess", "Oracle.EntityFrameworkCore", "oracledb"],
        "postgres" or "postgresql" =>
            ["Npgsql", "Npgsql.EntityFrameworkCore.PostgreSQL", "pg"],
        "sqlserver" or "sql server" or "mssql" =>
            ["Microsoft.Data.SqlClient", "System.Data.SqlClient",
             "Microsoft.EntityFrameworkCore.SqlServer", "mssql"],
        "mysql" or "mariadb" =>
            ["MySqlConnector", "MySql.Data", "Pomelo.EntityFrameworkCore.MySql", "mysql2", "mysql"],
        "sqlite" =>
            ["Microsoft.Data.Sqlite", "System.Data.SQLite",
             "Microsoft.EntityFrameworkCore.Sqlite", "better-sqlite3", "sqlite3"],
        _ => [],
    };

    /// <summary>
    /// Runbook é RESPONSABILIDADE, não nome de arquivo: o que importa é existir documento que
    /// ensine a executar. Exigir `RUNBOOK.md` premiaria quem renomeia e reprovaria quem documenta.
    /// </summary>
    private static ProductEvidence InspectRunbook(IProductWorkspace workspace, DateTimeOffset now)
    {
        foreach (var candidate in workspace.Find("*.md", limit: 120))
        {
            var content = workspace.ReadText(candidate);
            if (content is not null && RunInstructions.IsMatch(content))
            {
                return Observed(
                    ProductEvidenceKind.RunbookPresent, true, workspace, now,
                    $"{candidate} documenta como executar a aplicação.", candidate);
            }
        }

        return Observed(
            ProductEvidenceKind.RunbookPresent, false, workspace, now,
            "Nenhum documento da entrega ensina a executar o produto.");
    }

    private static ProductEvidence Observed(
        ProductEvidenceKind kind,
        bool satisfied,
        IProductWorkspace workspace,
        DateTimeOffset now,
        string detail,
        string? artifact = null) =>
        new(kind, satisfied, detail,
            ProductEvidenceProvenanceRecord.FromRepository(workspace.CommitSha, now, artifact));

    private static string? FindEntryPoint(IProductWorkspace workspace, string directory)
    {
        string[] candidates =
        [
            "src/main.tsx", "src/main.ts", "src/main.jsx", "src/main.js",
            "src/index.tsx", "src/index.ts", "src/app/main.ts", "index.html",
            "app/layout.tsx", "src/App.tsx", "src/App.vue",
        ];

        foreach (var candidate in candidates)
        {
            var path = directory.Length == 0 ? candidate : $"{directory}/{candidate}";
            if (workspace.FileExists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// O framework aparece como DEPENDÊNCIA declarada, não como palavra solta no arquivo — um
    /// `package.json` que menciona "react" numa descrição não é um projeto React.
    /// </summary>
    private static bool DeclaresDependency(string manifest, string framework)
    {
        var needle = framework.Trim().ToLowerInvariant() switch
        {
            "react" => "react",
            "angular" => "@angular/core",
            "vue" => "vue",
            "svelte" => "svelte",
            "next" or "next.js" or "nextjs" => "next",
            var other => other,
        };

        return Regex.IsMatch(
            manifest,
            $"\"{Regex.Escape(needle)}\"\\s*:\\s*\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static bool IsMigrationPath(string path) =>
        path.Contains("migration", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("migrations/", StringComparison.OrdinalIgnoreCase);

    private static string? ExpectedTargetFramework(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime))
        {
            return null;
        }

        var match = Regex.Match(
            runtime, @"(\d+)(?:\.\d+)?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? $"net{match.Groups[1].Value}." : null;
    }

    private static readonly Regex HttpSurface = new(
        @"\[(HttpGet|HttpPost|HttpPut|HttpPatch|HttpDelete)|Map(Get|Post|Put|Patch|Delete|Group)\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex RunInstructions = new(
        @"(dotnet\s+run|npm\s+(run\s+)?(dev|start|build)|docker\s+compose\s+up|yarn\s+(dev|start)|pnpm\s+(dev|start))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
}
