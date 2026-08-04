using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// A defesa contra o modelo aprovar o próprio trabalho.
///
/// Sem estas regras, um agente que escrevesse "FrontendBuildPassed = true" abriria o portão com
/// uma frase. O gate compara a FORÇA da evidência com o mínimo que cada requisito exige, e amarra
/// a prova ao commit sobre o qual ela foi produzida.
/// </summary>
public sealed class ProductEvidenceProvenanceTests
{
    private const string Commit = "aaaaaaaabbbbbbbbccccccccdddddddd00000000";
    private const string OutroCommit = "1111111122222222333333334444444455555555";

    [Fact]
    public void EvidenciaSemProveniencaValeComoAfirmacaoDoAtor()
    {
        var evidence = new ProductEvidence(ProductEvidenceKind.FrontendBuild, true);

        Assert.Equal(ProductEvidenceProvenance.Declared, evidence.Level);
    }

    [Fact]
    public void AgenteQueDeclaraQueBuildouNaoSatisfazORequisitoTecnico()
    {
        // O cenário exato do §25: o executor afirma que passou, e ninguém executou nada.
        var declarado = Enum.GetValues<ProductEvidenceKind>()
            .Select(kind => new ProductEvidence(
                kind, true, "o agente afirma que executou",
                ProductEvidenceProvenanceRecord.FromActor("worker-claude-secondary", "card-1")))
            .ToArray();

        var verdict = ProductDeliveryGate.Evaluate(Web(), declarado, Commit);

        Assert.False(verdict.Satisfied);
        Assert.All(verdict.Findings, finding => Assert.Equal(ProductEvidenceGap.Declared, finding.Gap));
        Assert.Contains(
            verdict.Findings,
            finding => finding.Reason.Contains("não satisfaz requisito técnico", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistenciaSeSatisfazComConstatacaoMasFuncionamentoExigeVerificacao()
    {
        var observado = Enum.GetValues<ProductEvidenceKind>()
            .Select(kind => new ProductEvidence(
                kind, true, null,
                ProductEvidenceProvenanceRecord.FromRepository(Commit, DateTimeOffset.UnixEpoch)))
            .ToArray();

        var verdict = ProductDeliveryGate.Evaluate(Web(), observado, Commit);

        // Presença de frontend, API, migrations e runbook são constatáveis olhando a árvore.
        Assert.DoesNotContain(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.FrontendPresent);
        Assert.DoesNotContain(verdict.Findings, finding => finding.Kind == ProductEvidenceKind.RunbookPresent);

        // Build, testes, jornada e integração não: olhar não prova que funciona.
        Assert.Contains(verdict.Findings, finding =>
            finding.Kind == ProductEvidenceKind.FrontendBuild && finding.Gap == ProductEvidenceGap.Declared);
        Assert.Contains(verdict.Findings, finding =>
            finding.Kind == ProductEvidenceKind.E2EJourneyPassed && finding.Gap == ProductEvidenceGap.Declared);
    }

    [Fact]
    public void EvidenciaDeOutroCommitNaoAprovaEsteCommit()
    {
        // Verificar A, mudar para B e aprovar B com a prova de A é o mesmo que não verificar.
        var deOutroCommit = ProductDeliveryGateTests.Complete(OutroCommit).ToArray();

        var verdict = ProductDeliveryGate.Evaluate(Web(), deOutroCommit, Commit);

        Assert.False(verdict.Satisfied);
        Assert.All(verdict.Findings, finding => Assert.Equal(ProductEvidenceGap.Stale, finding.Gap));
        Assert.Contains(
            verdict.Findings,
            finding => finding.Reason.Contains("11111111", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenciaDoMesmoCommitEAceita()
    {
        var verdict = ProductDeliveryGate.Evaluate(
            Web(), ProductDeliveryGateTests.Complete(Commit).ToArray(), Commit);

        Assert.True(verdict.Satisfied);
    }

    [Fact]
    public void AfirmacaoDoAtorNaoEnfraqueceUmaVerificacaoReal()
    {
        // O ator pode dizer o que quiser ao lado de uma verificação executada; a prova mais forte
        // é que decide. O que ele NÃO consegue é substituir a verificação por texto.
        var evidencias = ProductDeliveryGateTests.Complete(Commit)
            .Append(new ProductEvidence(
                ProductEvidenceKind.AutomatedTestsPassed, true, "o agente também afirma",
                ProductEvidenceProvenanceRecord.FromActor("worker")))
            .ToArray();

        Assert.True(ProductDeliveryGate.Evaluate(Web(), evidencias, Commit).Satisfied);
    }

    [Fact]
    public void QualquerReprovacaoVenceQualquerAprovacaoDoMesmoTipo()
    {
        var evidencias = ProductDeliveryGateTests.Complete(Commit)
            .Append(new ProductEvidence(
                ProductEvidenceKind.AutomatedTestsPassed, false, "dotnet test → 3 falhas",
                ProductEvidenceProvenanceRecord.FromVerifier(
                    "test-runner", Commit, DateTimeOffset.UnixEpoch, "dotnet test", 1)))
            .ToArray();

        var finding = Assert.Single(ProductDeliveryGate.Evaluate(Web(), evidencias, Commit).Findings);
        Assert.Equal(ProductEvidenceKind.AutomatedTestsPassed, finding.Kind);
        Assert.Equal(ProductEvidenceGap.Failed, finding.Gap);
    }

    [Fact]
    public void AProvenienciaDeUmVerificadorCarregaComandoESaida()
    {
        var provenance = ProductEvidenceProvenanceRecord.FromVerifier(
            "dotnet-build", Commit, DateTimeOffset.UnixEpoch, "dotnet build -c Release", 0, "exec-1");

        Assert.Equal(ProductEvidenceProvenance.Verified, provenance.Level);
        Assert.Equal("dotnet build -c Release", provenance.Command);
        Assert.Equal(0, provenance.ExitCode);
        Assert.Equal("exec-1", provenance.ExecutionId);
    }

    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs("project", "Crie um sistema simples de empréstimos.", []));
}
