using Harness.Host.Product;
using Harness.Modules.Workflows.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// Os verificadores rodando de verdade. Estes testes compilam projetos .NET reais dentro de uma
/// worktree temporária — é o que separa "o gate simula Verified" de "o Poseidon verificou".
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class ProductVerifierBehavior : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-verify-{Guid.NewGuid():N}");

    private const string Commit = "abcdef0123456789abcdef0123456789abcdef01";

    public ProductVerifierBehavior() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task BackendQueCompilaProduzEvidenciaVerifiedComExitZero()
    {
        WriteProject("src/Emprestimos.Api", compiles: true);

        var record = await new DotNetBuildVerifier(new TrustedProcessRunner())
            .VerifyAsync(Context(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.True(record.Succeeded, record.Detail);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal("dotnet-build", record.Verifier);
        Assert.Equal(Commit, record.CommitSha);
        Assert.Contains("dotnet build", record.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendQueNaoCompilaProduzEvidenciaNegativa()
    {
        WriteProject("src/Emprestimos.Api", compiles: false);

        var record = await new DotNetBuildVerifier(new TrustedProcessRunner())
            .VerifyAsync(Context(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.NotEqual(0, record.ExitCode);
    }

    [Fact]
    public async Task SuperficieAmbiguaProduzDiagnosticoEmVezDeEscolhaAleatoria()
    {
        // Duas solutions: escolher uma no sorteio produziria evidência sobre outro produto.
        File.WriteAllText(Path.Combine(_root, "Um.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "Dois.sln"), string.Empty);

        var record = await new DotNetBuildVerifier(new TrustedProcessRunner())
            .VerifyAsync(Context(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("ambígua", record.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntregaSemProjetoDeTesteNaoProduzEvidenciaDeSuitePassando()
    {
        // `dotnet test` numa solution sem testes sai com zero. Aceitar isso seria dizer "os testes
        // passaram" sobre zero testes — a mentira mais confortável que este gate poderia contar.
        WriteProject("src/Emprestimos.Api", compiles: true);

        var record = await new DotNetTestVerifier(new TrustedProcessRunner())
            .VerifyAsync(Context(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("Nenhum projeto de teste", record.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrontendComFrameworkDoPerfilEEncontradoEOSemScriptDeBuildReprova()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        File.WriteAllText(
            Path.Combine(_root, "frontend", "package.json"),
            """{"dependencies":{"react":"^18.3.1"}}""");

        var record = await new FrontendBuildVerifier(new TrustedProcessRunner())
            .VerifyAsync(Context(Web()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("não declara script `build`", record.Detail!, StringComparison.Ordinal);
        Assert.Equal("frontend", record.Artifact);
    }

    [Fact]
    public async Task FrontendComFrameworkDiferenteDoPerfilNaoServeComoEntrega()
    {
        // O perfil exige Angular; a entrega trouxe React. Não é o produto que a arquitetura decidiu.
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        File.WriteAllText(
            Path.Combine(_root, "frontend", "package.json"),
            """{"scripts":{"build":"vite build"},"dependencies":{"react":"^18.3.1"}}""");

        var record = await new FrontendBuildVerifier(new TrustedProcessRunner())
            .VerifyAsync(Context(Angular()), CancellationToken.None);

        Assert.NotNull(record);
        Assert.False(record.Succeeded);
        Assert.Contains("Angular", record.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverrideParaAngularFazOVerificadorProcurarAngularENaoReact()
    {
        Directory.CreateDirectory(Path.Combine(_root, "web"));
        File.WriteAllText(
            Path.Combine(_root, "web", "package.json"),
            """{"scripts":{"build":"ng build"},"dependencies":{"@angular/core":"^18.0.0"}}""");

        var record = await new FrontendBuildVerifier(new TrustedProcessRunner())
            .VerifyAsync(Context(Angular()), CancellationToken.None);

        Assert.NotNull(record);
        // O manifesto Angular foi ACEITO como a entrega (o build em si depende de `npm` e das
        // dependências instaladas; o que este teste trava é a resolução dirigida pelo perfil).
        Assert.DoesNotContain("Nenhum manifesto", record.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("não declara script", record.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void OPlanoDeVerificacaoEDerivadoDoPerfilEExplicaCadaExigencia()
    {
        var plano = ProductVerificationPlan.From(Web());

        Assert.Contains(ProductEvidenceKind.FrontendBuild, plano.Required);
        Assert.Contains(ProductEvidenceKind.E2EJourneyPassed, plano.Required);
        Assert.All(
            plano.Steps.Where(step => step.Required),
            step => Assert.False(string.IsNullOrWhiteSpace(step.Rationale)));

        var apiOnly = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("p", "Quero somente uma API de empréstimos.", []));
        var planoApi = ProductVerificationPlan.From(apiOnly);

        Assert.DoesNotContain(ProductEvidenceKind.FrontendBuild, planoApi.Required);
        Assert.Contains(ProductEvidenceKind.FrontendBuild, planoApi.NotApplicable);
        Assert.Contains("frontendbuild=n/a", planoApi.Summary(), StringComparison.Ordinal);
    }

    private ProductVerificationContext Context(ProjectEffectiveProfile profile) =>
        new(profile, _root, Commit, "projeto", "tentativa", "card");

    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("p", "Crie um sistema simples de empréstimos.", []));

    private static ProjectEffectiveProfile Angular() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("p", "Crie um sistema simples de empréstimos.",
            [new ProfileDirective(
                ProfileAuthority.ProjectRequirement,
                EffectiveProfileResolver.AreaFrontendFramework, "Angular", "Padronização")]));

    /// <summary>Projeto .NET mínimo e real — o verificador vai compilá-lo de verdade.</summary>
    private void WriteProject(string relativeDirectory, bool compiles)
    {
        var directory = Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "Emprestimos.Api.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(directory, "Program.cs"),
            compiles
                ? "Console.WriteLine(\"emprestimos\");"
                : "Console.WriteLine(\"emprestimos\"  // parêntese não fechado: não compila");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Build deixa handles abertos em alguns sistemas; o diretório é temporário.
            }
        }
    }
}
