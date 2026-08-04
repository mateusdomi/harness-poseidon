using Harness.Host.Product;
using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Product;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// A Fase 3 deixa de calcular o perfil sob demanda e passa a MATERIALIZÁ-LO, e o portão do produto
/// passa a incidir sobre a entrega. Estes testes cobrem a transição explícita: projeto governado
/// pelo baseline é cobrado; projeto anterior a ele é isentado COM NOME, nunca em silêncio.
/// </summary>
public sealed class ProductDeliveryEvaluatorBehavior
{
    private const string Tenant = "tenant-produto";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FB1";
    private const string Pedido = "Crie um sistema simples de empréstimos.";
    private const string Commit = "d4d4d4d4e5e5e5e5f6f6f6f60707070708080808";

    [Fact]
    public async Task AFaseDeArquiteturaMaterializaOPerfilAPartirDaDemanda()
    {
        await using var fixture = await Fixture.CreateAsync();

        var outcome = await fixture.Evaluator.EvaluateAsync(
            Tenant, Project, repositoryRoot: null, Commit,
            ProductDeliveryEvaluator.ArchitecturePhaseOrder, CancellationToken.None, Pedido);

        Assert.Null(outcome.Failure);
        Assert.NotNull(outcome.Profile);
        Assert.Equal(1, outcome.Profile.Version);
        Assert.Equal("Web", outcome.Profile.Modality);

        var profile = ProjectEffectiveProfile.FromJson(outcome.Profile.ProfileJson);
        Assert.Equal("React", profile!.Frontend.Framework);
        Assert.True(profile.Frontend.Required);
    }

    [Fact]
    public async Task MaterializarDuasVezesNaoCriaVersaoNova()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Evaluator.EvaluateAsync(
            Tenant, Project, null, Commit,
            ProductDeliveryEvaluator.ArchitecturePhaseOrder, CancellationToken.None, Pedido);
        await fixture.Evaluator.EvaluateAsync(
            Tenant, Project, null, Commit,
            ProductDeliveryEvaluator.ArchitecturePhaseOrder, CancellationToken.None, Pedido);

        Assert.Single(await fixture.Store.ListAsync(Tenant, Project));
    }

    [Fact]
    public async Task PerfilQueNaoDecidiuAModalidadeReprovaAArquitetura()
    {
        // Um pedido que não diz que produto é este não pode fechar a Arquitetura: sem modalidade,
        // "pronto" fica sem critério.
        await using var fixture = await Fixture.CreateAsync();

        var outcome = await fixture.Evaluator.EvaluateAsync(
            Tenant, Project, null, Commit,
            ProductDeliveryEvaluator.ArchitecturePhaseOrder, CancellationToken.None,
            "faça aquilo que combinamos");

        Assert.Equal(ProductDeliveryFailures.ProfileUndecided, outcome.Failure);
    }

    [Fact]
    public async Task ProjetoAnteriorAoBaselineEIsentadoComNomeNaoEmSilencio()
    {
        // §22/§23: compatibilidade legada não pode virar bypass anônimo.
        await using var fixture = await Fixture.CreateAsync();

        var outcome = await fixture.Evaluator.EvaluateAsync(
            Tenant, Project, null, Commit,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None,
            demandText: null);

        Assert.Equal(ProductDeliveryFailures.LegacyProjectExempt, outcome.Failure);
        Assert.Null(outcome.Verdict);
    }

    [Fact]
    public async Task EntregaSoComApiReprovaOPortaoDoProduto()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = fixture.WriteDelivery(includeFrontend: false);

        var outcome = await fixture.Evaluator.EvaluateAsync(
            Tenant, Project, repository, Commit,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None, Pedido);

        Assert.Equal(ProductDeliveryFailures.DeliveryIncomplete, outcome.Failure);
        Assert.NotNull(outcome.Verdict);
        Assert.False(outcome.Verdict.Satisfied);
        Assert.Contains(
            outcome.Verdict.Findings,
            finding => finding.Kind == ProductEvidenceKind.FrontendPresent);
    }

    [Fact]
    public async Task ArvoreSemRepositorioNaoViraAprovacaoPorFaltaDeVerificacao()
    {
        await using var fixture = await Fixture.CreateAsync();

        var outcome = await fixture.Evaluator.EvaluateAsync(
            Tenant, Project, "/caminho/que/nao/existe", Commit,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None, Pedido);

        Assert.Equal(ProductDeliveryFailures.DeliveryIncomplete, outcome.Failure);
        Assert.False(outcome.Verdict!.Satisfied);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteWriteDispatcher _dispatcher;
        private readonly string _root;

        private Fixture(SqliteWriteDispatcher dispatcher, string root)
        {
            _dispatcher = dispatcher;
            _root = root;
            Store = new SqliteProjectEffectiveProfileStore(dispatcher);
            Evaluator = new ProductDeliveryEvaluator(Store, SystemClock.Instance);
        }

        public SqliteProjectEffectiveProfileStore Store { get; }

        public ProductDeliveryEvaluator Evaluator { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                AppContext.BaseDirectory, "integration-artifacts", $"delivery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "harness.db"), CancellationToken.None);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, CancellationToken.None);
            return new Fixture(dispatcher, root);
        }

        /// <summary>Escreve em disco a árvore que o inspetor vai examinar.</summary>
        public string WriteDelivery(bool includeFrontend)
        {
            var repository = Path.Combine(_root, "produto");
            Directory.CreateDirectory(Path.Combine(repository, "src", "Emprestimos.Api"));
            File.WriteAllText(
                Path.Combine(repository, "src", "Emprestimos.Api", "Emprestimos.Api.csproj"),
                "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(
                Path.Combine(repository, "src", "Emprestimos.Api", "Program.cs"),
                "app.MapGet(\"/emprestimos\", () => new { emprestimos = Array.Empty<object>() });");

            if (includeFrontend)
            {
                Directory.CreateDirectory(Path.Combine(repository, "frontend", "src"));
                File.WriteAllText(
                    Path.Combine(repository, "frontend", "package.json"),
                    """{"dependencies":{"react":"^18.3.1"}}""");
                File.WriteAllText(
                    Path.Combine(repository, "frontend", "src", "main.tsx"), "createRoot(el).render(<App />);");
            }

            return repository;
        }

        public async ValueTask DisposeAsync() => await _dispatcher.DisposeAsync();
    }
}
