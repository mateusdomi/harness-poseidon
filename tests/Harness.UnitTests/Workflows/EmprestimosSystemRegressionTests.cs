using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// A pergunta que decide se esta fase terminou:
///
/// <i>se amanhã eu pedir "crie um sistema simples de empréstimos" e o Poseidon produzir apenas uma
/// API, existe um mecanismo INDEPENDENTE DO MODELO capaz de observar que o frontend obrigatório
/// não existe e impedir que o produto seja declarado pronto?</i>
///
/// Este teste percorre a cadeia inteira sobre a árvore realmente entregue — pedido → perfil →
/// coleta → gate → portão da fase — sem nenhuma evidência fornecida por agente. Se ele ficar
/// verde com a entrega incompleta, o enforcement caiu.
/// </summary>
public sealed class EmprestimosSystemRegressionTests
{
    private const string Pedido = "Crie um sistema simples de empréstimos.";

    [Fact]
    public void OPedidoSemStackResolveOBaselineCompleto()
    {
        var profile = Resolve();

        Assert.Equal(ProductModality.Web, profile.Modality);
        Assert.Equal(".NET 8", profile.Backend.Runtime);
        Assert.Equal("React", profile.Frontend.Framework);
        Assert.Equal("TypeScript", profile.Frontend.Language);
        Assert.Equal("Vite", profile.Frontend.BuildSystem);
        Assert.Equal("SQL Server", profile.Data.Database);
        Assert.Equal("REST", profile.Api.Protocol);
        Assert.True(profile.Api.OpenApiRequired);
    }

    [Fact]
    public void EntregaSoComApiEReprovadaSemQueNinguemInformeEvidencia()
    {
        var profile = Resolve();
        var workspace = RepositoryEvidenceCollectorTests.ApiOnlyDelivery();

        // Nenhuma evidência declarada: tudo vem da inspeção da árvore.
        var evidence = new ProductEvidenceCollectorPipeline().Collect(profile, workspace);
        var verdict = ProductDeliveryGate.Evaluate(profile, evidence.Items, workspace.CommitSha);

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, finding =>
            finding.Kind == ProductEvidenceKind.FrontendPresent &&
            finding.Gap == ProductEvidenceGap.Failed);
        Assert.Contains("repository-scanner", evidence.Collectors);

        var decision = PhaseGatePolicy.Decide(
            ProjectOperationMode.Autonomous, "5-Desenvolvimento", "gate", null,
            new PhaseGateEvidence(true, true, false, false, 4, verdict));

        Assert.Equal(PhaseGateDecision.NotReady, decision);
    }

    [Fact]
    public void EntregaCompletaPassaQuandoAsVerificacoesRealmenteRodaram()
    {
        var profile = Resolve();
        var workspace = RepositoryEvidenceCollectorTests.CompleteDelivery();

        // Constatação da árvore + verificações executadas pelo mecanismo controlado.
        var evidence = new ProductEvidenceCollectorPipeline().Collect(
            profile, workspace, Verificacoes(workspace.CommitSha));
        var verdict = ProductDeliveryGate.Evaluate(profile, evidence.Items, workspace.CommitSha);

        Assert.True(verdict.Satisfied);
        Assert.Contains("verification-log", evidence.Collectors);

        var decision = PhaseGatePolicy.Decide(
            ProjectOperationMode.Autonomous, "5-Desenvolvimento", "gate", null,
            new PhaseGateEvidence(true, true, false, false, 4, verdict));

        Assert.Equal(PhaseGateDecision.ChiefApproves, decision);
    }

    [Fact]
    public void EntregaCompletaNoRepositorioMasSemVerificacaoContinuaReprovada()
    {
        // Ter o código não é ter o produto funcionando: a árvore completa satisfaz existência e
        // forma, e reprova em tudo o que exige execução.
        var profile = Resolve();
        var workspace = RepositoryEvidenceCollectorTests.CompleteDelivery();

        var evidence = new ProductEvidenceCollectorPipeline().Collect(profile, workspace);
        var verdict = ProductDeliveryGate.Evaluate(profile, evidence.Items, workspace.CommitSha);

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.FrontendBuild);
        Assert.Contains(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.E2EJourneyPassed);
        Assert.DoesNotContain(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.FrontendPresent);
    }

    [Fact]
    public void OAtorNaoConsegueFabricarAAprovacaoDaPropriaEntregaIncompleta()
    {
        // O executor entrega só a API e declara que está tudo pronto. O inspetor contradiz.
        var profile = Resolve();
        var workspace = RepositoryEvidenceCollectorTests.ApiOnlyDelivery();
        var declaradoPeloAtor = Enum.GetValues<ProductEvidenceKind>()
            .Select(kind => new ProductEvidence(
                kind, true, "entreguei tudo",
                ProductEvidenceProvenanceRecord.FromActor("worker-claude-secondary")))
            .ToArray();

        var evidence = new ProductEvidenceCollectorPipeline().Collect(
            profile, workspace, verifications: null, declared: declaradoPeloAtor);
        var verdict = ProductDeliveryGate.Evaluate(profile, evidence.Items, workspace.CommitSha);

        Assert.False(verdict.Satisfied);
        Assert.Contains(verdict.Findings, finding =>
            finding.Kind == ProductEvidenceKind.FrontendPresent &&
            finding.Gap == ProductEvidenceGap.Failed);
    }

    [Fact]
    public void ApiOnlyExplicitaPassaSemFrontend()
    {
        var apiOnly = EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("project", "Crie somente uma API de empréstimos.", []));
        var files = RepositoryEvidenceCollectorTests.ApiOnlyDeliveryFiles();
        files["src/Emprestimos.Infrastructure/Migrations/0001_inicial.sql"] = "CREATE TABLE emprestimos (id int);";
        files["README.md"] = "Execute com `dotnet run`.";
        var workspace = new FakeProductWorkspace(RepositoryEvidenceCollectorTests.CommitSha, files);

        var evidence = new ProductEvidenceCollectorPipeline().Collect(
            apiOnly, workspace, Verificacoes(workspace.CommitSha));
        var verdict = ProductDeliveryGate.Evaluate(apiOnly, evidence.Items, workspace.CommitSha);

        Assert.True(verdict.Satisfied);
        Assert.DoesNotContain(ProductEvidenceKind.FrontendPresent, verdict.Required);
    }

    private static IReadOnlyList<ProductVerificationRecord> Verificacoes(string commit) =>
    [
        Verificacao(ProductEvidenceKind.BackendBuild, "dotnet build -c Release", commit),
        Verificacao(ProductEvidenceKind.SecurityScanPassed, "dotnet list package --vulnerable", commit),
        Verificacao(ProductEvidenceKind.FrontendBuild, "npm run build", commit),
        Verificacao(ProductEvidenceKind.FrontendBackendIntegration, "npm run test:integration", commit),
        Verificacao(ProductEvidenceKind.OpenApiGenerated, "dotnet swagger tofile", commit),
        Verificacao(ProductEvidenceKind.PersistenceVerified, "dotnet test --filter Persistence", commit),
        Verificacao(ProductEvidenceKind.AutomatedTestsPassed, "dotnet test", commit),
        Verificacao(ProductEvidenceKind.E2EJourneyPassed, "npx playwright test", commit),
    ];

    private static ProductVerificationRecord Verificacao(
        ProductEvidenceKind kind, string command, string commit) =>
        new(kind, true, "poseidon-runner", command, 0, commit, DateTimeOffset.UnixEpoch, "exec-1");

    private static ProjectEffectiveProfile Resolve() =>
        EffectiveProfileResolver.Resolve(new EffectiveProfileInputs("project", Pedido, []));
}
