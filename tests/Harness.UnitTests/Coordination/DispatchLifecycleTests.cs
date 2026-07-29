using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class DispatchLifecycleTests
{
    /// <summary>Gate da fase: escalação encerra o turno mas NÃO é falha.</summary>
    [Fact]
    public void EscalationEndsTheTurnWithoutBeingAFailure()
    {
        Assert.True(DispatchLifecycleTypes.EndsTurn(DispatchLifecycleTypes.Escalation));
        Assert.False(DispatchLifecycleTypes.IsFailureBearing(DispatchLifecycleTypes.Escalation));
    }

    [Fact]
    public void WorkerDoneEndsTheTurnAndCarriesTheOutcome()
    {
        Assert.True(DispatchLifecycleTypes.EndsTurn(DispatchLifecycleTypes.WorkerDone));
        Assert.True(DispatchLifecycleTypes.IsFailureBearing(DispatchLifecycleTypes.WorkerDone));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("dispatch")]
    [InlineData("heartbeat")]
    [InlineData("checkpoint")]
    [InlineData("decision_gate")]
    [InlineData("merge_ready")]
    public void NonTerminalTypesDoNotEndTheTurn(string messageType)
    {
        Assert.True(DispatchLifecycleTypes.All.Contains(messageType));
        Assert.False(DispatchLifecycleTypes.EndsTurn(messageType));
    }

    [Fact]
    public void DecisionGateIsADistinctTypeFromEscalation()
    {
        // decision_gate e a decisao de PLANO da Bruna; escalation e o agente parando para perguntar.
        // Colapsar os dois faz a Bruna esperar por um humano que ninguem chamou.
        Assert.NotEqual(DispatchLifecycleTypes.DecisionGate, DispatchLifecycleTypes.Escalation);
        Assert.False(DispatchLifecycleTypes.EndsTurn(DispatchLifecycleTypes.DecisionGate));
        Assert.True(DispatchLifecycleTypes.EndsTurn(DispatchLifecycleTypes.Escalation));
    }

    [Fact]
    public void LegacyCompletionIsAcceptedAndNormalizedToWorkerDone()
    {
        Assert.True(DispatchLifecycleTypes.All.Contains(DispatchLifecycleTypes.LegacyCompletion));
        Assert.Equal(
            DispatchLifecycleTypes.WorkerDone,
            DispatchLifecycleTypes.Canonical(DispatchLifecycleTypes.LegacyCompletion));
        Assert.Equal(
            DispatchLifecycleTypes.Heartbeat,
            DispatchLifecycleTypes.Canonical(DispatchLifecycleTypes.Heartbeat));
    }
}

public sealed class TurnCompletionPolicyTests
{
    private static readonly TurnCompletionState Fresh = new("attempt-7");

    /// <summary>Gate da fase: a falha também conclui — silêncio nunca é desfecho.</summary>
    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("escalated")]
    public void EveryOutcomeKindCompletesTheTurnExactlyOnce(string kind)
    {
        Assert.True(TurnCompletionPolicy.IsCompletionKind(kind));

        var first = TurnCompletionPolicy.Report(Fresh, kind, "chave-1");
        Assert.Equal(TurnCompletionOutcome.Applied, first.Outcome);
        Assert.True(first.State.Completed);
        Assert.Equal(kind, first.State.CompletionKind);

        var second = TurnCompletionPolicy.Report(first.State, kind, "chave-2");
        Assert.Equal(TurnCompletionOutcome.AlreadyCompleted, second.Outcome);
        Assert.Equal(kind, second.State.CompletionKind);
    }

    [Fact]
    public void ResendingTheSameCompletionIsAcceptedWithoutDuplicatingEffect()
    {
        var first = TurnCompletionPolicy.Report(Fresh, "succeeded", "chave-1");
        var replay = TurnCompletionPolicy.Report(first.State, "succeeded", "chave-1");

        Assert.Equal(TurnCompletionOutcome.IdempotentReplay, replay.Outcome);
        Assert.Equal(TurnCompletionPolicy.ReasonReplay, replay.ReasonCode);
        Assert.Equal(first.State, replay.State);
    }

    [Fact]
    public void ASecondVersionOfTheStoryCannotRewriteTheFirst()
    {
        var failed = TurnCompletionPolicy.Report(Fresh, "failed", "chave-1");
        var laterSuccess = TurnCompletionPolicy.Report(failed.State, "succeeded", "chave-9");

        Assert.Equal(TurnCompletionOutcome.AlreadyCompleted, laterSuccess.Outcome);
        Assert.Equal("failed", laterSuccess.State.CompletionKind);
    }

    [Fact]
    public void SameKeyWithADifferentOutcomeIsNotTreatedAsReplay()
    {
        var first = TurnCompletionPolicy.Report(Fresh, "succeeded", "chave-1");
        var conflicting = TurnCompletionPolicy.Report(first.State, "failed", "chave-1");

        Assert.Equal(TurnCompletionOutcome.AlreadyCompleted, conflicting.Outcome);
        Assert.Equal("succeeded", conflicting.State.CompletionKind);
    }

    [Fact]
    public void SilenceIsNotACompletionKind()
    {
        Assert.False(TurnCompletionPolicy.IsCompletionKind("timeout"));
        Assert.False(TurnCompletionPolicy.IsCompletionKind("abandoned"));
    }
}
