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

    /// <summary>
    /// O defeito que teria custado a madrugada de 2026-08-03: havia três bloqueios externos
    /// registrados (contas sem credencial) e o supervisor devolvia "chame o proprietário" no
    /// primeiro ciclo — com quatro defeitos que a Integradora fazia sozinha e a prova limpa
    /// parada na fase 3 de 9. Bloqueio externo não é permissão para ir dormir.
    /// </summary>
    [Fact]
    public void AnExternalBlockerDoesNotWakeTheOwnerWhileTheAgentStillHasItsOwnWork()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-030","status":"open","owner":"human","nextAction":"PROPRIETARIO: decidir credencial"}""",
            """{"id":"OPS-032","status":"open","nextAction":"Criar o sessions/ no provisionamento"}""",
        ]);

        var state = (Complete() with
        {
            ExternalBlockers = ["contas sem credencial no conteiner"],
            HumanDecisionRequired = true,
        }).WithFindings(findings);

        Assert.Equal(2, state.ExecutableWork);
        Assert.Equal(1, state.AgentExecutableWork);
        Assert.False(CompletionGate.NeedsHuman(state));
    }

    /// <summary>
    /// Nem a prova em aberto: enquanto um eixo não passou, existe trabalho da Integradora
    /// mesmo sem nenhum finding do agente na lista.
    /// </summary>
    [Fact]
    public void AnUnfinishedProofIsAgentWorkEvenWithoutAnyAgentFinding()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-028","status":"open","owner":"human","nextAction":"EXTERNO: esperar reset de cota"}""",
        ]);

        var state = (Complete() with
        {
            CleanE2E = new E2EState { Status = "running", Phase = 3 },
            ExternalBlockers = ["cota semanal esgotada"],
        }).WithFindings(findings);

        Assert.Equal(0, state.AgentExecutableWork);
        Assert.False(CompletionGate.NeedsHuman(state));
    }

    /// <summary>
    /// O caso legítimo: só sobrou o que o dono destrava. Aí sim relançar sessão é queimar
    /// cota sem produzir nada.
    /// </summary>
    [Fact]
    public void WhenOnlyOwnerWorkRemainsTheSupervisorStopsRelaunching()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-030","status":"open","owner":"human","nextAction":"PROPRIETARIO: decidir credencial"}""",
        ]);

        var state = (Complete() with { ExternalBlockers = ["credencial que so o dono gera"] })
            .WithFindings(findings);

        Assert.True(CompletionGate.NeedsHuman(state));
        Assert.False(CompletionGate.Evaluate(state).Passed);
    }

    /// <summary>
    /// Sem dono declarado o defeito é do agente. O default importa: se um finding novo
    /// nascesse "do humano" por omissão, a operação pararia sozinha ao registrá-lo.
    /// </summary>
    [Fact]
    public void AFindingWithoutADeclaredOwnerBelongsToTheAgent()
    {
        var findings = OperationFinding.ParseLines(
        [
            """{"id":"OPS-999","status":"open","nextAction":"corrigir"}""",
        ]);

        Assert.Equal(1, Complete().WithFindings(findings).AgentExecutableWork);
    }

    [Fact]
    public void StateSurvivesARoundTripThroughJson()
    {
        var restored = OperationState.Parse(Complete().ToJson());

        Assert.Equal("pass", restored.CleanE2E?.Status);
        Assert.Equal("pass", restored.GeneratedProduct);
    }
}
