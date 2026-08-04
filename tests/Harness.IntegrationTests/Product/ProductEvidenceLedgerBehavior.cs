using Harness.Host.Product;
using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Product;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// O ledger de evidência. A pergunta que ele existe para responder: "exatamente quais evidências
/// fizeram este projeto passar?" — meses depois, sem depender da memória de ninguém.
/// </summary>
[Collection(ProcessVerificationGroup.Name)]
public sealed class ProductEvidenceLedgerBehavior : IAsyncDisposable
{
    private const string Tenant = "tenant-ledger";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FC2";
    private const string Pedido = "Crie um sistema simples de empréstimos.";
    private const string CommitA = "1a1a1a1a2b2b2b2b3c3c3c3c4d4d4d4d5e5e5e5e";
    private const string CommitB = "9f9f9f9f8e8e8e8e7d7d7d7d6c6c6c6c5b5b5b5b";

    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "integration-artifacts", $"ledger-{Guid.NewGuid():N}");
    private SqliteWriteDispatcher? _dispatcher;

    [Fact]
    public async Task OConjuntoQueDecidiuOPortaoFicaPersistidoEConsultavel()
    {
        var (evaluator, sets) = await CreateAsync();
        Directory.CreateDirectory(Path.Combine(_root, "produto"));

        var outcome = await evaluator.EvaluateAsync(
            Tenant, Project, Path.Combine(_root, "produto"), CommitA,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None, Pedido,
            "tentativa-17", "card-3");

        Assert.NotNull(outcome.EvidenceSetId);
        var stored = await sets.GetAsync(Tenant, outcome.EvidenceSetId!);

        Assert.NotNull(stored);
        Assert.Equal(Project, stored.ProjectId);
        Assert.Equal(CommitA, stored.CommitSha);
        Assert.Equal("failed", stored.GateDecision);
        Assert.Equal("Web", stored.Modality);
        Assert.Equal(1, stored.ProfileVersion);
        Assert.Equal("tentativa-17", stored.AttemptId);
        Assert.Equal("card-3", stored.TaskId);
        Assert.Contains("FrontendPresent", stored.ItemsJson, StringComparison.Ordinal);
        Assert.Contains("repository-scanner", stored.Collectors, StringComparison.Ordinal);
        Assert.Contains("frontendPresent", stored.PlanJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Missing", stored.FindingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATentativaQueReprovouNaoDesapareceQuandoASeguintePassa()
    {
        // Sobrescrever o conjunto anterior apagaria justamente o que a fábrica precisa para
        // aprender por que a primeira tentativa falhou.
        var (evaluator, sets) = await CreateAsync();
        var produto = Path.Combine(_root, "produto");
        Directory.CreateDirectory(produto);

        var primeira = await evaluator.EvaluateAsync(
            Tenant, Project, produto, CommitA,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None, Pedido,
            "tentativa-17");
        var segunda = await evaluator.EvaluateAsync(
            Tenant, Project, produto, CommitB,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None, Pedido,
            "tentativa-18");

        Assert.NotEqual(primeira.EvidenceSetId, segunda.EvidenceSetId);

        var historico = await sets.ListAsync(Tenant, Project, 10);
        Assert.Equal(2, historico.Count);
        Assert.Contains(historico, item => item.CommitSha == CommitA && item.AttemptId == "tentativa-17");
        Assert.Contains(historico, item => item.CommitSha == CommitB && item.AttemptId == "tentativa-18");
    }

    [Fact]
    public async Task OConjuntoDeUmTenantNaoVazaParaOutro()
    {
        var (evaluator, sets) = await CreateAsync();
        Directory.CreateDirectory(Path.Combine(_root, "produto"));

        var outcome = await evaluator.EvaluateAsync(
            Tenant, Project, Path.Combine(_root, "produto"), CommitA,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None, Pedido);

        Assert.Null(await sets.GetAsync("outro-tenant", outcome.EvidenceSetId!));
        Assert.Empty(await sets.ListAsync("outro-tenant", Project, 10));
    }

    [Fact]
    public async Task OConjuntoCarregaOPerfilVigenteEOSeuFingerprint()
    {
        var (evaluator, sets) = await CreateAsync();
        Directory.CreateDirectory(Path.Combine(_root, "produto"));

        var outcome = await evaluator.EvaluateAsync(
            Tenant, Project, Path.Combine(_root, "produto"), CommitA,
            ProductDeliveryEvaluator.DevelopmentPhaseOrder, CancellationToken.None, Pedido);

        var stored = await sets.GetAsync(Tenant, outcome.EvidenceSetId!);
        Assert.Equal(outcome.Profile!.Fingerprint, stored!.ProfileFingerprint);
        Assert.Equal(outcome.Profile.Version, stored.ProfileVersion);
    }

    private async Task<(ProductDeliveryEvaluator Evaluator, IProductEvidenceSetStore Sets)> CreateAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(
            Path.Combine(_root, "harness.db"), CancellationToken.None);
        await SqliteMigrationRunner.ApplyAsync(_dispatcher, CancellationToken.None);

        var profiles = new SqliteProjectEffectiveProfileStore(_dispatcher);
        var sets = new SqliteProductEvidenceSetStore(_dispatcher);
        return (
            new ProductDeliveryEvaluator(profiles, SystemClock.Instance, null, null, null, sets),
            sets);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dispatcher is not null)
        {
            await _dispatcher.DisposeAsync();
        }
    }
}
