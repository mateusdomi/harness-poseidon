using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>Árvore entregue em memória: o inspetor é testável sem tocar em disco.</summary>
internal sealed class FakeProductWorkspace(string commitSha, Dictionary<string, string> files)
    : IProductWorkspace
{
    public string CommitSha { get; } = commitSha;

    public bool FileExists(string relativePath) => files.ContainsKey(relativePath);

    public bool DirectoryExists(string relativePath) =>
        files.Keys.Any(path => path.StartsWith(relativePath + "/", StringComparison.Ordinal));

    public IReadOnlyList<string> Find(string pattern, int limit = 200)
    {
        var suffix = pattern.StartsWith('*') ? pattern[1..] : pattern;
        return files.Keys
            .Where(path => pattern.StartsWith('*')
                ? path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                : path.Equals(pattern, StringComparison.Ordinal) ||
                    path.EndsWith("/" + pattern, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    public string? ReadText(string relativePath) =>
        files.TryGetValue(relativePath, out var content) ? content : null;
}

/// <summary>
/// O elo que a Fase 3 existe para fechar: EXECUÇÃO → EVIDÊNCIA. Estes testes provam que o Poseidon
/// constata fatos da árvore entregue em vez de acreditar no que o executor escreveu.
/// </summary>
public sealed class RepositoryEvidenceCollectorTests
{
    private const string Commit = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";

    [Fact]
    public void FrontendObrigatorioAusenteEConstatadoComoAusente()
    {
        var evidence = Collect(Web(), ApiOnlyDelivery());

        var frontend = Single(evidence, ProductEvidenceKind.FrontendPresent);
        Assert.False(frontend.Satisfied);
        Assert.Equal(ProductEvidenceProvenance.Observed, frontend.Level);
        Assert.Equal(Commit, frontend.Provenance!.CommitSha);
    }

    [Fact]
    public void PastaChamadaFrontendNaoEProvaDeFrontend()
    {
        // O nome da pasta é a forma mais fácil de enganar um inspetor ingênuo.
        var files = ApiOnlyDeliveryFiles();
        files["frontend/LEIA-ME.txt"] = "aqui vai o frontend algum dia";

        var frontend = Single(Collect(Web(), new FakeProductWorkspace(Commit, files)),
            ProductEvidenceKind.FrontendPresent);

        Assert.False(frontend.Satisfied);
    }

    [Fact]
    public void ManifestoSemPontoDeEntradaNaoEInterface()
    {
        var files = ApiOnlyDeliveryFiles();
        files["frontend/package.json"] = """{"dependencies":{"react":"^18.3.1"}}""";

        var frontend = Single(Collect(Web(), new FakeProductWorkspace(Commit, files)),
            ProductEvidenceKind.FrontendPresent);

        Assert.False(frontend.Satisfied);
        Assert.Contains("ponto de entrada", frontend.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontendCompletoEConstatado()
    {
        var frontend = Single(Collect(Web(), CompleteDelivery()), ProductEvidenceKind.FrontendPresent);

        Assert.True(frontend.Satisfied);
        Assert.Equal("frontend/package.json", frontend.Provenance!.Artifact);
    }

    [Fact]
    public void OverrideDeFrameworkEValidadoContraOPerfilNaoContraUmLiteral()
    {
        // Projeto que sobrescreveu React por Angular não pode reprovar por não ter React.
        var angular = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "project", "Sistema de empréstimos.",
            [new ProfileDirective(
                ProfileAuthority.ProjectRequirement,
                EffectiveProfileResolver.AreaFrontendFramework, "Angular", "Padronização")]));

        var files = ApiOnlyDeliveryFiles();
        files["frontend/package.json"] = """{"dependencies":{"@angular/core":"^18.0.0"}}""";
        files["frontend/src/main.ts"] = "bootstrapApplication(AppComponent);";

        var frontend = Single(Collect(angular, new FakeProductWorkspace(Commit, files)),
            ProductEvidenceKind.FrontendPresent);

        Assert.True(frontend.Satisfied);
        Assert.Contains("Angular", frontend.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactEntregueOndeOPerfilExigeAngularNaoSatisfaz()
    {
        var angular = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "project", "Sistema de empréstimos.",
            [new ProfileDirective(
                ProfileAuthority.ProjectRequirement,
                EffectiveProfileResolver.AreaFrontendFramework, "Angular", "Padronização")]));

        var frontend = Single(Collect(angular, CompleteDelivery()), ProductEvidenceKind.FrontendPresent);

        Assert.False(frontend.Satisfied);
    }

    [Fact]
    public void BackendComOutroRuntimeNaoEOBackendQueOPerfilDecidiu()
    {
        var files = CompleteDeliveryFiles();
        files["src/Emprestimos.Api/Emprestimos.Api.csproj"] =
            "<Project><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>";

        var backend = Single(Collect(Web(), new FakeProductWorkspace(Commit, files)),
            ProductEvidenceKind.BackendPresent);

        Assert.False(backend.Satisfied);
        Assert.Contains("net8.", backend.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiEDetectadaPelaSuperficieHttpNaoPeloNomeDaPasta()
    {
        var api = Single(Collect(Web(), CompleteDelivery()), ProductEvidenceKind.ApiPresent);
        Assert.True(api.Satisfied);

        var files = CompleteDeliveryFiles();
        files["src/Emprestimos.Api/Program.cs"] = "var app = builder.Build(); app.Run();";
        var semEndpoint = Single(Collect(Web(), new FakeProductWorkspace(Commit, files)),
            ProductEvidenceKind.ApiPresent);
        Assert.False(semEndpoint.Satisfied);
    }

    [Fact]
    public void MigrationsSaoConstatadasQuandoOPerfilExigePersistencia()
    {
        Assert.True(Single(Collect(Web(), CompleteDelivery()),
            ProductEvidenceKind.DatabaseMigrationValidated).Satisfied);
        Assert.False(Single(Collect(Web(), ApiOnlyDelivery()),
            ProductEvidenceKind.DatabaseMigrationValidated).Satisfied);
    }

    [Fact]
    public void RunbookEResponsabilidadeNaoNomeDeArquivo()
    {
        // Exigir `RUNBOOK.md` premiaria quem renomeia e reprovaria quem documenta.
        var files = ApiOnlyDeliveryFiles();
        files["LEIAME.md"] = "# Como executar\n\n`dotnet run --project src/Emprestimos.Api`\n";

        Assert.True(Single(Collect(Web(), new FakeProductWorkspace(Commit, files)),
            ProductEvidenceKind.RunbookPresent).Satisfied);
    }

    [Fact]
    public void ToucaEvidenciaConstatadaCarregaOCommitVerificado()
    {
        foreach (var item in Collect(Web(), CompleteDelivery()))
        {
            Assert.Equal(Commit, item.Provenance!.CommitSha);
            Assert.Equal("repository-scanner", item.Provenance.Source);
        }
    }

    internal static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("project", "Crie um sistema simples de empréstimos.", []));

    private static IReadOnlyList<ProductEvidence> Collect(
        ProjectEffectiveProfile profile, IProductWorkspace workspace) =>
        new RepositoryEvidenceCollector().Collect(profile, workspace);

    private static ProductEvidence Single(
        IReadOnlyList<ProductEvidence> evidence, ProductEvidenceKind kind) =>
        Assert.Single(evidence, item => item.Kind == kind);

    /// <summary>Exatamente o que a fábrica entregou no incidente: uma API e nada mais.</summary>
    internal static Dictionary<string, string> ApiOnlyDeliveryFiles() => new(StringComparer.Ordinal)
    {
        ["src/Emprestimos.Api/Emprestimos.Api.csproj"] =
            "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
        ["src/Emprestimos.Api/Program.cs"] =
            "app.MapGet(\"/emprestimos\", () => new { emprestimos = Array.Empty<object>() });",
    };

    internal static FakeProductWorkspace ApiOnlyDelivery() => new(Commit, ApiOnlyDeliveryFiles());

    internal static Dictionary<string, string> CompleteDeliveryFiles()
    {
        var files = ApiOnlyDeliveryFiles();
        files["frontend/package.json"] = """{"dependencies":{"react":"^18.3.1","vite":"^5.4.0"}}""";
        files["frontend/src/main.tsx"] = "createRoot(document.getElementById('root')!).render(<App />);";
        files["src/Emprestimos.Infrastructure/Migrations/0001_inicial.sql"] =
            "CREATE TABLE emprestimos (id uniqueidentifier PRIMARY KEY);";
        files["README.md"] = "# Empréstimos\n\nExecute com `dotnet run` e `npm run dev`.\n";
        return files;
    }

    internal static FakeProductWorkspace CompleteDelivery() => new(Commit, CompleteDeliveryFiles());

    internal const string CommitSha = Commit;
}
