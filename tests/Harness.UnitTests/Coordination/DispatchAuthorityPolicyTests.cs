using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class DispatchAuthorityPolicyTests
{
    private static readonly ActiveDispatch Active = new("card-1", "attempt-7", FencingToken: 42);

    [Fact]
    public void MessageFromTheActiveAttemptIsAccepted()
    {
        var decision = DispatchAuthorityPolicy.Decide(
            new ClaimedIdentity("card-1", "attempt-7", 42, "runner-a"), Active);

        Assert.True(decision.IsAccepted);
        Assert.Equal(WorkProvenance.Orchestrated, decision.Provenance);
        Assert.False(decision.RequiresAudit);
    }

    /// <summary>
    /// Gate da fase: o agente reiniciou, tem identificador NOVO e o mesmo fencing — a conclusão
    /// dele é aceita. Era exatamente este caso que o protocolo anterior jogava fora.
    /// </summary>
    [Fact]
    public void RestartedAgentWithNewProcessIdentifierButSameFencingIsAccepted()
    {
        var decision = DispatchAuthorityPolicy.DecideWithRestartAwareness(
            new ClaimedIdentity("card-1", "attempt-7", 42, RunnerId: "runner-b-apos-reinicio"),
            Active,
            dispatchedRunnerId: "runner-a-original");

        Assert.True(decision.IsAccepted);
        Assert.Equal(WorkProvenance.Orchestrated, decision.Provenance);
        Assert.Equal(DispatchAuthorityPolicy.ReasonAcceptedAfterRestart, decision.ReasonCode);
        Assert.False(decision.RequiresAudit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("runner-a")]
    [InlineData("runner-qualquer-outro")]
    public void ProcessIdentifierNeverChangesTheVerdict(string? runnerId)
    {
        var decision = DispatchAuthorityPolicy.Decide(
            new ClaimedIdentity("card-1", "attempt-7", 42, runnerId), Active);

        Assert.True(decision.IsAccepted);
    }

    [Fact]
    public void MessageFromAnotherAttemptIsRejectedAndAudited()
    {
        var decision = DispatchAuthorityPolicy.Decide(
            new ClaimedIdentity("card-1", "attempt-OUTRA", 42, "runner-a"), Active);

        Assert.False(decision.IsAccepted);
        Assert.Equal(DispatchAuthorityVerdict.RejectedForeignAttempt, decision.Verdict);
        Assert.True(decision.RequiresAudit);
        Assert.Equal(WorkProvenance.Unorchestrated, decision.Provenance);
    }

    [Fact]
    public void MessageFromAnotherCardIsRejectedAndAudited()
    {
        var decision = DispatchAuthorityPolicy.Decide(
            new ClaimedIdentity("card-OUTRO", "attempt-7", 42, "runner-a"), Active);

        Assert.Equal(DispatchAuthorityVerdict.RejectedForeignAttempt, decision.Verdict);
        Assert.True(decision.RequiresAudit);
    }

    [Fact]
    public void LateResultFromASupersededAttemptIsRejected()
    {
        var decision = DispatchAuthorityPolicy.Decide(
            new ClaimedIdentity("card-1", "attempt-7", 41, "runner-a"), Active);

        Assert.Equal(DispatchAuthorityVerdict.RejectedStaleFencing, decision.Verdict);
        Assert.True(decision.RequiresAudit);
    }

    [Fact]
    public void FencingAheadOfTheDispatchIsRejected()
    {
        var decision = DispatchAuthorityPolicy.Decide(
            new ClaimedIdentity("card-1", "attempt-7", 43, "runner-a"), Active);

        Assert.Equal(DispatchAuthorityVerdict.RejectedUnknownFencing, decision.Verdict);
        Assert.True(decision.RequiresAudit);
    }

    /// <summary>Gate da fase: trabalho sem despacho ativo nunca é redescrito como orquestrado.</summary>
    [Fact]
    public void WorkWithoutAnActiveDispatchIsRecordedAsUnorchestrated()
    {
        var decision = DispatchAuthorityPolicy.Decide(
            new ClaimedIdentity("card-1", "attempt-7", 42, "runner-a"), active: null);

        Assert.False(decision.IsAccepted);
        Assert.Equal(DispatchAuthorityVerdict.RejectedNoActiveDispatch, decision.Verdict);
        Assert.Equal(WorkProvenance.Unorchestrated, decision.Provenance);
        Assert.True(decision.RequiresAudit);
    }

    [Fact]
    public void EveryRejectionCarriesUnorchestratedProvenance()
    {
        ClaimedIdentity[] rejected =
        [
            new("card-1", "attempt-OUTRA", 42),
            new("card-OUTRO", "attempt-7", 42),
            new("card-1", "attempt-7", 1),
            new("card-1", "attempt-7", 9_999)
        ];

        Assert.All(
            rejected.Select(identity => DispatchAuthorityPolicy.Decide(identity, Active)),
            decision =>
            {
                Assert.False(decision.IsAccepted);
                Assert.Equal(WorkProvenance.Unorchestrated, decision.Provenance);
                Assert.True(decision.RequiresAudit);
            });
    }
}
