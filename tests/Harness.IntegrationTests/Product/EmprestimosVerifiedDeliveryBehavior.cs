using Harness.Host.Product;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// A prova final da Fase 4: o Poseidon CONSTRÓI, TESTA e VALIDA a entrega por conta própria, e só
/// evidência do commit efetivamente verificado libera o Definition of Done.
///
/// Nenhum cenário aqui injeta `Verified` à mão. Os processos rodam de verdade — `dotnet build`,
/// `dotnet test`, `npm run …` — dentro de uma worktree temporária, com allowlist de executável e
/// teto de tempo.
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class EmprestimosVerifiedDeliveryBehavior : IDisposable
{
    private const string Pedido = "Crie um sistema simples de empréstimos.";
    private const string CommitA = "aaaa000011112222333344445555666677778888";
    private const string CommitB = "bbbb000011112222333344445555666677778888";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-emprestimos-{Guid.NewGuid():N}");

    public EmprestimosVerifiedDeliveryBehavior() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CenarioAProdutoWebComSomenteApiReprova()
    {
        EscreverBackend(compila: true, comTestes: true);
        EscreverScripts();

        var (verdict, evidence) = await AvaliarAsync(Web(), CommitA);

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, f => f.Kind == ProductEvidenceKind.FrontendPresent);
        Assert.Contains(verdict.Findings, f => f.Kind == ProductEvidenceKind.FrontendBuild);

        // O backend, esse, foi realmente compilado e testado.
        Assert.Contains(evidence.Items, item =>
            item.Kind == ProductEvidenceKind.BackendBuild &&
            item.Satisfied &&
            item.Level == ProductEvidenceProvenance.Verified);
    }

    [Fact]
    public void CenarioBFrontendEBackendExistemMasNadaFoiVerificadoReprova()
    {
        // Presença não é funcionamento: a árvore está completa e nenhum verificador rodou.
        EscreverBackend(compila: true, comTestes: true);
        EscreverFrontend("react");
        EscreverRunbook();

        var workspace = new Harness.Host.Product.FileSystemProductWorkspace(_root, CommitA);
        var evidence = new ProductEvidenceCollectorPipeline().Collect(Web(), workspace);
        var verdict = ProductDeliveryGate.Evaluate(Web(), evidence.Items, CommitA);

        Assert.False(verdict.Satisfied);
        Assert.DoesNotContain(verdict.Findings, f => f.Kind == ProductEvidenceKind.FrontendPresent);
        Assert.Contains(verdict.Findings, f => f.Kind == ProductEvidenceKind.BackendBuild);
        Assert.Contains(verdict.Findings, f => f.Kind == ProductEvidenceKind.E2EJourneyPassed);
    }

    [Fact]
    public async Task CenarioCEntregaWebCompletaVerificadaPeloPoseidonAprova()
    {
        EscreverBackend(compila: true, comTestes: true);
        EscreverFrontend("react");
        EscreverScripts();
        EscreverMigrations();
        EscreverRunbook();

        var (verdict, evidence) = await AvaliarAsync(Web(), CommitA);

        Assert.True(verdict.Satisfied, Diagnostico(verdict, evidence));
        Assert.All(
            evidence.Items.Where(item => item.Level == ProductEvidenceProvenance.Verified),
            item => Assert.Equal(CommitA, item.Provenance!.CommitSha));

        var decision = PhaseGatePolicy.Decide(
            ProjectOperationMode.Autonomous, "5-Desenvolvimento", "gate", null,
            new PhaseGateEvidence(true, true, false, false, 4, verdict));
        Assert.Equal(PhaseGateDecision.ChiefApproves, decision);
    }

    [Fact]
    public async Task CenarioDApiOnlyCompletaSemFrontendAprova()
    {
        EscreverBackend(compila: true, comTestes: true);
        EscreverScripts();
        EscreverMigrations();
        EscreverRunbook();

        var (verdict, evidence) = await AvaliarAsync(ApiOnly(), CommitA);

        Assert.True(verdict.Satisfied, Diagnostico(verdict, evidence));
        Assert.DoesNotContain(ProductEvidenceKind.FrontendPresent, verdict.Required);
    }

    [Fact]
    public async Task EvidenciaDoCommitAnteriorNaoAprovaOCommitAtual()
    {
        // Verificar A, mexer no código (agora é B) e reaproveitar a prova de A é o mesmo que não
        // verificar. É a trava que impede aprovar código que ninguém checou.
        EscreverBackend(compila: true, comTestes: true);
        EscreverFrontend("react");
        EscreverScripts();
        EscreverMigrations();
        EscreverRunbook();

        var (_, evidenceA) = await AvaliarAsync(Web(), CommitA);
        var verdictSobreB = ProductDeliveryGate.Evaluate(Web(), evidenceA.Items, CommitB);

        Assert.False(verdictSobreB.Satisfied);
        Assert.All(verdictSobreB.Findings, f => Assert.Equal(ProductEvidenceGap.Stale, f.Gap));
    }

    private async Task<(ProductDeliveryVerdict Verdict, ProductDeliveryEvidence Evidence)> AvaliarAsync(
        ProjectEffectiveProfile profile, string commit)
    {
        var runner = new TrustedProcessRunner();
        var verifiers = new List<IProductVerifier>
        {
            new DotNetBuildVerifier(runner),
            new DotNetTestVerifier(runner),
            new FrontendBuildVerifier(runner),
        };
        verifiers.AddRange(ScriptedVerifierCatalog.Create(runner));

        var plan = ProductVerificationPlan.From(profile);
        var verified = await new ProductVerificationRunner(verifiers).RunAsync(
            new ProductVerificationContext(profile, _root, commit, "projeto", "tentativa", "card"),
            plan,
            CancellationToken.None);

        var workspace = new Harness.Host.Product.FileSystemProductWorkspace(_root, commit);
        var evidence = new ProductEvidenceCollectorPipeline().Collect(profile, workspace, verified);
        return (ProductDeliveryGate.Evaluate(profile, evidence.Items, commit), evidence);
    }

    private static string Diagnostico(ProductDeliveryVerdict verdict, ProductDeliveryEvidence evidence) =>
        verdict.Summary() + "\n" + string.Join(
            '\n', evidence.Items.Select(item => $"  {item.Kind} {item.Satisfied} {item.Level} {item.Detail}"));

    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("projeto", Pedido, []));

    private static ProjectEffectiveProfile ApiOnly() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("projeto", "Crie somente uma API de empréstimos.", []));

    private void EscreverBackend(bool compila, bool comTestes)
    {
        var api = Path.Combine(_root, "src", "Emprestimos.Api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Emprestimos.Api.csproj"), WebCsproj());
        File.WriteAllText(
            Path.Combine(api, "Program.cs"),
            compila
                ? """
                  var builder = WebApplication.CreateBuilder(args);
                  var app = builder.Build();
                  app.MapGet("/emprestimos", () => Array.Empty<object>());
                  app.Run();
                  """
                : "Console.WriteLine(\"x\"  // não compila");

        if (!comTestes)
        {
            return;
        }

        var tests = Path.Combine(_root, "tests", "Emprestimos.Tests");
        Directory.CreateDirectory(tests);
        File.WriteAllText(Path.Combine(tests, "Emprestimos.Tests.csproj"), Csproj(exe: true));
        File.WriteAllText(Path.Combine(tests, "Program.cs"), "Console.WriteLine(\"sem casos\");");

        // A solution existe para que a superfície a verificar seja INEQUÍVOCA: com dois projetos
        // soltos, o verificador recusa por ambiguidade em vez de escolher um no sorteio.
        File.WriteAllText(
            Path.Combine(_root, "Emprestimos.slnx"),
            """
            <Solution>
              <Project Path="src/Emprestimos.Api/Emprestimos.Api.csproj" />
              <Project Path="tests/Emprestimos.Tests/Emprestimos.Tests.csproj" />
            </Solution>
            """);
    }

    /// <summary>
    /// Projeto real e mínimo. Console em vez de suíte xUnit de propósito: baixar pacote exigiria
    /// rede, e o runner de verificação roda offline — a entrega precisa ser verificável sem sair
    /// para a internet.
    /// </summary>
    private static string WebCsproj() =>
        """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private static string Csproj(bool exe) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>{(exe ? "Exe" : "Library")}</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
        </Project>
        """;

    private void EscreverFrontend(string framework)
    {
        var frontend = Path.Combine(_root, "frontend");
        Directory.CreateDirectory(Path.Combine(frontend, "src"));
        File.WriteAllText(
            Path.Combine(frontend, "package.json"),
            $$"""
            {
              "name": "emprestimos-web",
              "private": true,
              "scripts": {
                "build": "node -e \"process.exit(0)\"",
                "test:e2e": "node -e \"process.exit(0)\"",
                "test:integration": "node -e \"process.exit(0)\"",
                "test:persistence": "node -e \"process.exit(0)\"",
                "openapi": "node -e \"process.exit(0)\""
              },
              "dependencies": { "{{framework}}": "^18.3.1" }
            }
            """);
        File.WriteAllText(
            Path.Combine(frontend, "src", "main.tsx"),
            "createRoot(document.getElementById('root')!).render(<App />);");
    }

    /// <summary>
    /// Os scripts convencionados que a entrega declara. O NOME vem do catálogo do Poseidon; o
    /// conteúdo é da entrega — é o que uma CI faz.
    /// </summary>
    private void EscreverScripts()
    {
        if (File.Exists(Path.Combine(_root, "frontend", "package.json")))
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(_root, "package.json"),
            """
            {
              "name": "emprestimos",
              "private": true,
              "scripts": {
                "test:e2e": "node -e \"process.exit(0)\"",
                "test:integration": "node -e \"process.exit(0)\"",
                "test:persistence": "node -e \"process.exit(0)\"",
                "openapi": "node -e \"process.exit(0)\""
              }
            }
            """);
    }

    private void EscreverMigrations()
    {
        var migrations = Path.Combine(_root, "src", "Emprestimos.Infrastructure", "Migrations");
        Directory.CreateDirectory(migrations);
        File.WriteAllText(
            Path.Combine(migrations, "0001_inicial.sql"),
            "CREATE TABLE emprestimos (id uniqueidentifier PRIMARY KEY);");
    }

    private void EscreverRunbook() => File.WriteAllText(
        Path.Combine(_root, "README.md"),
        "# Empréstimos\n\nExecute com `dotnet run` e `npm run dev`. Porta 8099.\n");

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // O build deixa handles abertos em alguns sistemas; o diretório é temporário.
        }
    }
}
