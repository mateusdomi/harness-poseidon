using Harness.Modules.Agents.Application.Accounts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O estado recuperável pertence ao CARD, não à tentativa nem ao ator. O modelo anterior exigia o
/// mesmo alias na continuação — e contradizia exatamente o caso que precisava cobrir: quando a
/// cota esgota, continuar significa trocar de conta.
/// </summary>
public sealed class CheckpointResumePolicyTests
{
    private static CheckpointResumeVerdict Evaluate(
        CheckpointOrigin origin,
        string candidateAccount = "worker-b",
        string candidateRole = "backend-specialist",
        bool executorChanged = false) =>
        CheckpointResumePolicy.Evaluate(
            origin, "backend-specialist", candidateRole, "worker-a", candidateAccount, executorChanged);

    [Fact]
    public void QuotaExhaustionLetsAnotherAccountContinue()
    {
        var verdict = Evaluate(CheckpointOrigin.Quota);

        Assert.True(verdict.Allowed);
        Assert.Equal("resume.other_account", verdict.ReasonCode);
        Assert.True(verdict.ExactContinuation);
    }

    [Fact]
    public void TheSameAccountComingBackIsAlsoAValidResume()
    {
        var verdict = Evaluate(CheckpointOrigin.Transient, candidateAccount: "worker-a");

        Assert.True(verdict.Allowed);
        Assert.Equal("resume.same_account", verdict.ReasonCode);
    }

    [Fact]
    public void ChangingExecutorIsAResumeButNeverAnExactOne()
    {
        // Formato de sessão e modelo de edição diferem entre adapters: o que se recupera é o
        // trabalho em disco mais o resumo, nunca a sessão do agente anterior. Afirmar "continuação
        // exata" aqui seria promessa não cumprida.
        var verdict = Evaluate(CheckpointOrigin.Quota, executorChanged: true);

        Assert.True(verdict.Allowed);
        Assert.Equal("resume.reconstructed_on_other_executor", verdict.ReasonCode);
        Assert.False(verdict.ExactContinuation);
    }

    [Fact]
    public void ACorrectionAskedByTheCriticStaysWithWhoProducedIt()
    {
        // Trocar o ator aqui apagaria a autoria do trabalho que está sendo corrigido e embaralharia
        // a segregação da revisão.
        var verdict = Evaluate(CheckpointOrigin.Review);

        Assert.False(verdict.Allowed);
        Assert.Equal("resume.review_requires_same_actor", verdict.ReasonCode);
        Assert.True(Evaluate(CheckpointOrigin.Review, candidateAccount: "worker-a").Allowed);
    }

    [Fact]
    public void CancellationIsNeverResumed()
    {
        var verdict = Evaluate(CheckpointOrigin.Cancelled, candidateAccount: "worker-a");

        Assert.False(verdict.Allowed);
        Assert.Equal("resume.cancelled", verdict.ReasonCode);
    }

    [Fact]
    public void TheRoleNeverChangesOnResume()
    {
        // O papel define o escopo de escrita: retomar sob outro papel mudaria em silêncio o que a
        // tentativa pode tocar.
        var verdict = Evaluate(CheckpointOrigin.Quota, candidateRole: "frontend-specialist");

        Assert.False(verdict.Allowed);
        Assert.Equal("resume.role_mismatch", verdict.ReasonCode);
    }

    [Fact]
    public void TheOriginRoundTripsThroughStorage()
    {
        foreach (var origin in Enum.GetValues<CheckpointOrigin>())
        {
            Assert.Equal(origin, CheckpointResumePolicy.ParseOrigin(CheckpointResumePolicy.Serialize(origin)));
        }

        // Origem desconhecida cai em transitória: nem autoriza troca de conta como a cota, nem
        // trava a retomada como o cancelamento.
        Assert.Equal(CheckpointOrigin.Transient, CheckpointResumePolicy.ParseOrigin("inventada"));
    }
}
