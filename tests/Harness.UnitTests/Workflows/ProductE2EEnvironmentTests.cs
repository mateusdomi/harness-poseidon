using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// O núcleo do ambiente efêmero do gate: gerar segredos e resolver ${VAR}. Determinístico dado o
/// gerador e o alocador — é o que tira o gate do "unavailable" sem pôr segredo no git.
/// </summary>
public sealed class ProductE2EEnvironmentTests
{
    private static ProductE2EHarness Harness(
        IReadOnlyDictionary<string, string>? secrets,
        string? dbPortVar,
        IReadOnlyDictionary<string, string> env) =>
        new(
            ComposeFile: "infra/docker-compose.yml", ComposeService: "oracle",
            ApiProject: "src/Api", ApiHealthPath: "/health", ApiUrl: "http://127.0.0.1:5199",
            FrontUrl: null, E2eDir: "frontend", E2eCommand: ["npx", "playwright", "test"],
            Env: env, GeneratedSecrets: secrets, DbPortVar: dbPortVar);

    [Fact]
    public void GeraSegredosEResolveTemplatesContraEles()
    {
        var harness = Harness(
            new Dictionary<string, string> { ["DB_SENHA"] = "password", ["JWT"] = "hex32" },
            dbPortVar: null,
            new Dictionary<string, string>
            {
                ["CONEXAO"] = "User Id=APP;Password=${DB_SENHA};Data Source=localhost/XE",
                ["JWT_CHAVE"] = "${JWT}",
            });

        // Gerador determinístico por tipo, para o teste afirmar o encaixe exato.
        var env = ProductE2EEnvironment.Materialize(
            harness,
            generateSecret: kind => kind == "hex32" ? "DEADBEEF" : "s3nha",
            allocatePort: () => 0);

        Assert.Equal("s3nha", env["DB_SENHA"]);
        Assert.Equal("DEADBEEF", env["JWT"]);
        Assert.Equal("User Id=APP;Password=s3nha;Data Source=localhost/XE", env["CONEXAO"]);
        Assert.Equal("DEADBEEF", env["JWT_CHAVE"]);
    }

    [Fact]
    public void PortaAleatoriaVaiParaAVariavelPedidaEParaDbPort()
    {
        var harness = Harness(
            secrets: null,
            dbPortVar: "ORACLE_PORT",
            new Dictionary<string, string>
            {
                ["CONEXAO"] = "Data Source=localhost:${DB_PORT}/FREEPDB1",
            });

        var env = ProductE2EEnvironment.Materialize(
            harness, generateSecret: _ => "x", allocatePort: () => 49152);

        Assert.Equal("49152", env["ORACLE_PORT"]);
        Assert.Equal("49152", env["DB_PORT"]);
        Assert.Equal("Data Source=localhost:49152/FREEPDB1", env["CONEXAO"]);
    }

    [Fact]
    public void TokenSemCorrespondenciaFicaLiteralNuncaExplode()
    {
        var harness = Harness(
            secrets: null, dbPortVar: null,
            new Dictionary<string, string> { ["X"] = "antes ${NAO_EXISTE} depois" });

        var env = ProductE2EEnvironment.Materialize(harness, _ => "v", () => 0);

        Assert.Equal("antes ${NAO_EXISTE} depois", env["X"]);
    }

    [Fact]
    public void ManifestoComPlaceholderSemProvedorFalhaAntesDeSubirAmbiente()
    {
        var harness = Harness(
            secrets: null, dbPortVar: null,
            new Dictionary<string, string>
            {
                ["ConnectionStrings__Default"] = "User Id=APP;Password=${DB_PASSWORD}",
            });

        var missing = ProductE2EEnvironment.MissingPlaceholders(harness);

        Assert.Equal(["DB_PASSWORD"], missing);
    }

    [Fact]
    public void ManifestoComSegredoGeradoNaoReportaPlaceholderAusente()
    {
        var harness = Harness(
            new Dictionary<string, string> { ["DB_PASSWORD"] = "password" },
            dbPortVar: "ORACLE_PORT",
            new Dictionary<string, string>
            {
                ["ConnectionStrings__Default"] = "Password=${DB_PASSWORD};Port=${DB_PORT}",
            });

        Assert.Empty(ProductE2EEnvironment.MissingPlaceholders(harness));
    }

    [Fact]
    public void SemSegredosNemPortaEhSoOEnvLiteral()
    {
        var harness = Harness(
            secrets: null, dbPortVar: null,
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });

        var env = ProductE2EEnvironment.Materialize(harness, _ => "x", () => 0);

        Assert.Equal("1", env["A"]);
        Assert.Equal("2", env["B"]);
    }
}
