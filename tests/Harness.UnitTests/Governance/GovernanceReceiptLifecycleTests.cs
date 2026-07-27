using Harness.Persistence.Abstractions.Governance;

namespace Harness.UnitTests.Governance;

/// <summary>
/// O ciclo do recibo de governança decide se um turno interrompido pode ser RETOMADO. Achado ao
/// vivo: um turno do chefe morto no meio da invocação (Host derrubado) deixava o recibo em
/// `delivered`; a nova tentativa refazia o caminho, pedia `delivered` de novo, era recusada como
/// transição inválida e o turno esgotava as três retentativas sem sair do lugar — a mensagem do
/// usuário ficava sem resposta para sempre.
/// </summary>
public sealed class GovernanceReceiptLifecycleTests
{
    [Fact]
    public void ARetryMayDeliverTheSameBundleAgain()
    {
        Assert.True(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Delivered, GovernanceReceiptState.Delivered));
    }

    [Fact]
    public void TheNormalPathRemainsIntact()
    {
        Assert.True(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Selected, GovernanceReceiptState.Delivered));
        Assert.True(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Selected, GovernanceReceiptState.Completed));
        Assert.True(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Delivered, GovernanceReceiptState.Completed));
        Assert.True(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Delivered, GovernanceReceiptState.Failed));
        Assert.True(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Failed, GovernanceReceiptState.Delivered));
    }

    [Fact]
    public void AConcludedReceiptIsNeverReopened()
    {
        // Abrir a repetição de `delivered` não pode virar licença para reabrir um veredito.
        foreach (var next in Enum.GetValues<GovernanceReceiptState>())
        {
            Assert.False(GovernanceReceiptLifecycle.CanTransition(
                GovernanceReceiptState.Completed, next));
        }

        Assert.False(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Failed, GovernanceReceiptState.Completed));
        Assert.False(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Failed, GovernanceReceiptState.Failed));
        Assert.False(GovernanceReceiptLifecycle.CanTransition(
            GovernanceReceiptState.Selected, GovernanceReceiptState.Selected));
    }
}
