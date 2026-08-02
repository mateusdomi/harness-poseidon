using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// Cada ramo desta política corresponde a um card que ficou travado de verdade em 2026-08-02.
/// </summary>
public sealed class AttemptReconciliationPolicyTests
{
    private static AttemptReconciliationFacts Facts(
        bool workspace = true,
        bool completed = false,
        bool dead = false,
        int ageMinutes = 60,
        bool richHarvest = false) =>
        new(workspace, completed, dead, TimeSpan.FromMinutes(ageMinutes), richHarvest);

    /// <summary>
    /// Tentativa sem workspace e velha: órfã. Sete delas abriram esta operação, uma parada
    /// havia seis dias.
    /// </summary>
    [Fact]
    public void AnAttemptWithoutAWorkspaceIsAnOrphanOnceTheToleranceIsPast()
    {
        Assert.Equal(
            AttemptReconciliationAction.ExpireOrphan,
            AttemptReconciliationPolicy.Decide(Facts(workspace: false)));
    }

    /// <summary>
    /// A janela existe porque o orquestrador aceita o run DEPOIS de a cadeia iniciá-lo: sem ela,
    /// um lançamento do próprio ciclo morreria no berço.
    /// </summary>
    [Fact]
    public void AFreshLaunchIsNotMistakenForAnOrphan()
    {
        Assert.Equal(
            AttemptReconciliationAction.None,
            AttemptReconciliationPolicy.Decide(Facts(workspace: false, ageMinutes: 1)));
    }

    [Fact]
    public void ADeadRunReturnsTheCardToTheQueue()
    {
        Assert.Equal(
            AttemptReconciliationAction.ExpireDead,
            AttemptReconciliationPolicy.Decide(Facts(dead: true)));
    }

    /// <summary>
    /// Projeto pausado ou manual: a colheita rica não roda, e o trabalho pronto precisa ser
    /// registrado aqui. Pausar impede trabalho NOVO; não apaga trabalho já feito.
    /// </summary>
    [Fact]
    public void FinishedWorkIsRecordedWhenTheRichHarvestWillNotRun()
    {
        Assert.Equal(
            AttemptReconciliationAction.RecordCompleted,
            AttemptReconciliationPolicy.Decide(Facts(completed: true, richHarvest: false)));
    }

    /// <summary>
    /// Com a colheita rica no mesmo ciclo, é ela quem registra — ela harmoniza a worktree e mede
    /// o consumo. Registrar duas vezes trocaria evidência completa por evidência pobre.
    /// </summary>
    [Fact]
    public void FinishedWorkIsLeftToTheRichHarvestWhenItWillRun()
    {
        Assert.Equal(
            AttemptReconciliationAction.None,
            AttemptReconciliationPolicy.Decide(Facts(completed: true, richHarvest: true)));
    }

    /// <summary>Execução ainda em voo não se toca, por mais velha que seja.</summary>
    [Fact]
    public void AnInFlightRunIsNeverTouched()
    {
        Assert.Equal(
            AttemptReconciliationAction.None,
            AttemptReconciliationPolicy.Decide(Facts(ageMinutes: 6000)));
    }
}
