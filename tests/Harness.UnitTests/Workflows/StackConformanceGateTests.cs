using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// O gate que a avaliação TrensRJ provou faltar: o intake resolveu React + .NET + Oracle, a
/// execução entregou Node com dados em memória, e quatro reviews independentes aprovaram —
/// porque todos olhavam o diff. Estes testes usam a ENTREGA REAL do incidente como fixture e
/// provam que o gate a reprova mecanicamente, e que a entrega conforme passa.
/// </summary>
public sealed class StackConformanceGateTests
{
    private const string Commit = "b7c9d1e3f5a7b9c1d3e5f7a9b1c3d5e7f9a1b3c5";

    /// <summary>Perfil equivalente ao do Indicadores: web, React, .NET baseline, Oracle Required.</summary>
    private static ProjectEffectiveProfile IndicadoresProfile() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs(
            "indicadores",
            "Sistema web de indicadores com dashboard, importação de planilhas e Oracle.",
            [
                new ProfileDirective(
                    ProfileAuthority.ProjectRequirement,
                    EffectiveProfileResolver.AreaDatabase,
                    "oracle",
                    "Utilizar banco de dados Oracle (levantamento do cliente)."),
            ]));

    /// <summary>
    /// A árvore que a fábrica REALMENTE entregou no incidente, em miniatura fiel: backend Node
    /// zero-dependency, adaptadores "oracle" sem driver, dados em memória, nenhum frontend.
    /// </summary>
    private static FakeProductWorkspace NodeInMemoryDelivery() => new(Commit, new(StringComparer.Ordinal)
    {
        ["package.json"] =
            """{"name":"indicadorest-backend","type":"module","dependencies":{},"scripts":{"start":"node src/backend/main.js"}}""",
        ["src/backend/main.js"] =
            "import { criarRepositoriosEmMemoria } from './infra/persistencia/memoria/repositorios.js';",
        ["src/backend/infra/persistencia/oracle/repositorios.js"] =
            "// Adaptadores Oracle. O driver concreto e injetado na composicao.",
        ["infra/oracle/migrations/0001_schema.sql"] =
            "CREATE TABLE usuarios (id VARCHAR2(26) PRIMARY KEY);",
        ["docs/instalacao.md"] = "# Instalação\n\nExecute com `npm start`.\n",
    });

    /// <summary>A entrega que o perfil pedia: .NET com driver Oracle, React com entrada, API HTTP.</summary>
    private static FakeProductWorkspace ConformantDelivery() => new(Commit, new(StringComparer.Ordinal)
    {
        ["src/Indicadores.Api/Indicadores.Api.csproj"] =
            "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><PackageReference Include=\"Oracle.ManagedDataAccess\" Version=\"23.5.0\" /></ItemGroup></Project>",
        ["src/Indicadores.Api/Program.cs"] =
            "app.MapGet(\"/api/v1/indicadores\", () => Results.Ok());",
        ["src/Indicadores.Infrastructure/Migrations/0001_inicial.sql"] =
            "CREATE TABLE usuarios (id VARCHAR2(26) PRIMARY KEY);",
        ["frontend/package.json"] = """{"dependencies":{"react":"^18.3.1","vite":"^5.4.0"}}""",
        ["frontend/src/main.tsx"] = "createRoot(document.getElementById('root')!).render(<App />);",
        ["README.md"] = "# Indicadores\n\nExecute com `dotnet run` e `npm run dev`.\n",
    });

    [Fact]
    public void EntregaRealDoIncidenteIndicadoresEReprovadaMecanicamente()
    {
        var profile = IndicadoresProfile();
        var evidence = new RepositoryEvidenceCollector().Collect(profile, NodeInMemoryDelivery());

        var verdict = StackConformanceGate.Evaluate(profile, evidence);

        Assert.False(verdict.Satisfied);
        var kinds = verdict.Violations.Select(violation => violation.Kind).ToArray();
        // Sem .csproj não há o backend que o perfil decidiu; sem manifesto React não há o
        // frontend; sem driver declarado o "Oracle" é fachada.
        Assert.Contains(ProductEvidenceKind.BackendPresent, kinds);
        Assert.Contains(ProductEvidenceKind.FrontendPresent, kinds);
        Assert.Contains(ProductEvidenceKind.DataAccessDeclared, kinds);
    }

    [Fact]
    public void EntregaConformeAoPerfilPassa()
    {
        var profile = IndicadoresProfile();
        var evidence = new RepositoryEvidenceCollector().Collect(profile, ConformantDelivery());

        var verdict = StackConformanceGate.Evaluate(profile, evidence);

        Assert.True(verdict.Satisfied);
        Assert.Empty(verdict.Violations);
        Assert.Equal("stack_conformant", verdict.Summary());
    }

    [Fact]
    public void MigrationsSemDriverDeclaradoNaoProvamAcessoAoBanco()
    {
        var profile = IndicadoresProfile();
        var evidence = new RepositoryEvidenceCollector().Collect(profile, NodeInMemoryDelivery());

        var dataAccess = Assert.Single(
            evidence, item => item.Kind == ProductEvidenceKind.DataAccessDeclared);
        var migrations = Assert.Single(
            evidence, item => item.Kind == ProductEvidenceKind.DatabaseMigrationValidated);

        // O incidente em uma linha: as migrations EXISTEM (e satisfazem), o driver NÃO — e é o
        // driver ausente que denuncia a persistência de fachada.
        Assert.True(migrations.Satisfied);
        Assert.False(dataAccess.Satisfied);
    }

    [Fact]
    public void BancoForaDoCatalogoDeDriversNaoReprova()
    {
        var profile = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs(
                "projeto",
                "Sistema web com banco corporativo proprietário.",
                [
                    new ProfileDirective(
                        ProfileAuthority.ProjectRequirement,
                        EffectiveProfileResolver.AreaDatabase,
                        "banco-proprietario-x",
                        "Cliente exige o banco proprietário."),
                ]));
        var evidence = new RepositoryEvidenceCollector().Collect(profile, ConformantDelivery());

        var dataAccess = Assert.Single(
            evidence, item => item.Kind == ProductEvidenceKind.DataAccessDeclared);

        // Nulo significa desconhecido: sem catálogo de drivers para o banco, não há prova de
        // divergência e a constatação não reprova.
        Assert.True(dataAccess.Satisfied);
    }

    [Fact]
    public void SummaryDeclaraOsKindsViolados()
    {
        var profile = IndicadoresProfile();
        var evidence = new RepositoryEvidenceCollector().Collect(profile, NodeInMemoryDelivery());

        var verdict = StackConformanceGate.Evaluate(profile, evidence);

        Assert.StartsWith("stack_nonconformant:", verdict.Summary(), StringComparison.Ordinal);
        Assert.Contains("dataaccessdeclared", verdict.Summary(), StringComparison.Ordinal);
    }
}
