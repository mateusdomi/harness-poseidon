using Harness.Modules.Workflows.Product;
using static Harness.Modules.Workflows.Product.ProjectAttentionClassifier;

namespace Harness.UnitTests.Product;

/// <summary>
/// O modelo de atenção do desenvolvedor: um estado por projeto, urgência decrescente, fonte única.
/// A tese multi-projeto morre se o operador precisar abrir cada chat para saber se algo espera
/// por ele — e o canal morre se avisar card normal.
/// </summary>
public sealed class ProjectAttentionTests
{
    private static ProjectAttentionFacts Base() => new(false, 0, false, 0, 0, false, false);

    [Fact]
    public void DecisaoHumanaVenceQualquerOutroEstado()
    {
        var state = Classify(Base() with { EscalatedCards = 1, RetryableFailures = 3, BlockedCards = 2 });
        Assert.Equal(ProjectAttentionState.NeedsHumanDecision, state);
    }

    [Fact]
    public void ProjetoPausadoNaoGritaPorAtencao()
    {
        var state = Classify(Base() with { Paused = true, EscalatedCards = 5 });
        Assert.Equal(ProjectAttentionState.AutonomouslyProgressing, state);
    }

    [Theory]
    [InlineData(true, false, ProjectAttentionState.DeliveryReady)]
    [InlineData(false, true, ProjectAttentionState.ReadyForUat)]
    public void EntregaEUatSaoEstadosProprios(bool complete, bool uat, ProjectAttentionState expected)
    {
        Assert.Equal(expected, Classify(Base() with { AllPhasesComplete = complete, InUatPhase = uat }));
    }

    [Fact]
    public void FalhaRetentavelNaoEBloqueio()
    {
        Assert.Equal(ProjectAttentionState.FailedRetryable, Classify(Base() with { RetryableFailures = 1 }));
        Assert.Equal(ProjectAttentionState.BlockedExternal, Classify(Base() with { BlockedCards = 1 }));
    }

    [Fact]
    public void SomenteDecisaoUatEEntregaSaoAltoSinal()
    {
        Assert.True(IsHighSignal(ProjectAttentionState.NeedsHumanDecision));
        Assert.True(IsHighSignal(ProjectAttentionState.ReadyForUat));
        Assert.True(IsHighSignal(ProjectAttentionState.DeliveryReady));
        Assert.False(IsHighSignal(ProjectAttentionState.AutonomouslyProgressing));
        Assert.False(IsHighSignal(ProjectAttentionState.FailedRetryable));
        Assert.False(IsHighSignal(ProjectAttentionState.BlockedExternal));
    }
}
