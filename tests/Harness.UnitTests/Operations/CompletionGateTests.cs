using Harness.Modules.Operations;

namespace Harness.UnitTests.Operations;

/// <summary>
/// R1 — a saída do agente não é autoritativa.
///
/// Em 2026-08-02 a instância Integradora emitiu um relatório final enquanto ainda conhecia
/// oito defeitos abertos, o E2E na fase 3 de 9 e dois cards falhando. Nenhuma quantidade de
/// "não pare" no prompt fecha essa lacuna, porque prompt é probabilidade e não mecanismo.
/// Estes testes fixam o mecanismo.
/// </summary>
public sealed class CompletionGateTests
{
    private static OperationState Complete() => new()
    {
        CleanE2E = new E2EState { Status = "pass", Phase = 9 },
        GeneratedProduct = "pass",
        RecoveryTests = "pass",
        MandatoryGatesFailed = 0,
        MandatoryTestsPending = 0,
    };

    [Fact]
    public void OperationOnlyFinishesWhenEveryConditionHolds()
    {
        var verdict = CompletionGate.Evaluate(Complete());

        Assert.True(verdict.Passed);
        Assert.Empty(verdict.Reasons);
    }

    [Fact]
    public void AnUnfinishedCleanE2ENeverPasses()
    {
        var state = Complete() with { CleanE2E = new E2EState { Status = "running", Phase = 2 } };

        var verdict = CompletionGate.Evaluate(state);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, reason => reason.Contains("prova limpa", StringComparison.Ordinal));
    }

    /// <summary>
    /// O caso exato do incidente: tudo "parece" pronto, mas há trabalho conhecido. O gate
    /// tem de recusar mesmo assim — é a única defesa contra um fechamento prematuro.
    /// </summary>
    [Fact]
    public void KnownExecutableWorkBlocksTheGateEvenWhenEverythingElseIsGreen()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-009","severity":"high","status":"open","nextAction":"Logar o diagnostico"}""",
        ]);

        var verdict = CompletionGate.Evaluate(Complete().WithFindings(findings));

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, reason => reason.Contains("executável", StringComparison.Ordinal));
    }

    [Fact]
    public void ABlockingFindingIsReportedSeparatelyFromOrdinaryWork()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-100","severity":"critical","status":"open","blocking":true,"nextAction":"corrigir"}""",
        ]);

        var verdict = CompletionGate.Evaluate(Complete().WithFindings(findings));

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, reason => reason.Contains("bloqueante", StringComparison.Ordinal));
    }

    /// <summary>
    /// Um defeito já corrigido não pode continuar segurando a operação — senão o gate nunca
    /// abriria e o supervisor entraria em laço eterno.
    /// </summary>
    [Fact]
    public void AFixedFindingStopsCountingAsWork()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-001","severity":"critical","status":"fixed","nextAction":"nada"}""",
        ]);

        Assert.True(CompletionGate.Evaluate(Complete().WithFindings(findings)).Passed);
    }

    /// <summary>
    /// Defeito aberto SEM ação conhecida não é trabalho executável: é registro. Contá-lo
    /// tornaria o gate impossível de fechar por causa de itens que ninguém sabe atacar.
    /// </summary>
    [Fact]
    public void AnOpenFindingWithoutANextActionIsRecordKeepingNotWork()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-200","severity":"low","status":"open"}""",
        ]);

        Assert.True(CompletionGate.Evaluate(Complete().WithFindings(findings)).Passed);
    }

    /// <summary>
    /// Uma linha corrompida não pode apagar defeitos do radar: ela vira um finding próprio.
    /// Zerar o contador por um caractere quebrado seria a pior forma de liberar a saída.
    /// </summary>
    [Fact]
    public void ACorruptedFindingLineBecomesAFindingInsteadOfDisappearing()
    {
        var findings = OperationFinding.ParseLines(["{ isto nao e json", """{"id":"OK","status":"fixed"}"""]);

        Assert.Contains(findings, finding => finding.Id == "PARSE-ERROR");
        Assert.False(CompletionGate.Evaluate(Complete().WithFindings(findings)).Passed);
    }

    [Fact]
    public void AnExternalBlockerAsksForTheOwnerInsteadOfRelaunchingForever()
    {
        var state = Complete() with { ExternalBlockers = ["credencial que so o dono gera"] };

        Assert.True(CompletionGate.NeedsHuman(state));
    }

    [Fact]
    public void StateSurvivesARoundTripThroughJson()
    {
        var restored = OperationState.Parse(Complete().ToJson());

        Assert.Equal("pass", restored.CleanE2E?.Status);
        Assert.Equal("pass", restored.GeneratedProduct);
    }
}
