using Harness.Modules.Conversations.Domain;

namespace Harness.UnitTests.Conversations;

public sealed class ChiefTurnActivityTests
{
    [Theory]
    [InlineData(ChiefTurnActivity.Received, "received")]
    [InlineData(ChiefTurnActivity.ReadingContext, "reading_context")]
    [InlineData(ChiefTurnActivity.Thinking, "thinking")]
    [InlineData(ChiefTurnActivity.Planning, "planning")]
    [InlineData(ChiefTurnActivity.Delegating, "delegating")]
    [InlineData(ChiefTurnActivity.AgentWorking, "agent_working")]
    [InlineData(ChiefTurnActivity.AwaitingReview, "awaiting_review")]
    [InlineData(ChiefTurnActivity.Blocked, "blocked")]
    [InlineData(ChiefTurnActivity.Completed, "completed")]
    [InlineData(ChiefTurnActivity.Failed, "failed")]
    public void WireNamesAreStableSnakeCase(ChiefTurnActivity activity, string expected) =>
        Assert.Equal(expected, ChiefTurnActivityState.Wire(activity));

    [Fact]
    public void EveryActivityHasAWireName() =>
        Assert.All(ChiefTurnActivityState.All, activity =>
            Assert.False(string.IsNullOrWhiteSpace(ChiefTurnActivityState.Wire(activity))));

    [Theory]
    [InlineData(ChiefTurnActivity.Blocked)]
    [InlineData(ChiefTurnActivity.Completed)]
    [InlineData(ChiefTurnActivity.Failed)]
    public void TerminalStatesHaveNoOutgoingTransition(ChiefTurnActivity terminal)
    {
        Assert.True(ChiefTurnActivityState.IsTerminal(terminal));
        Assert.All(ChiefTurnActivityState.All, to =>
            Assert.False(ChiefTurnActivityState.CanTransition(terminal, to)));
    }

    [Theory]
    // Caminho honesto do worker: contexto → pensa → planeja → delega/conclui.
    [InlineData(ChiefTurnActivity.Received, ChiefTurnActivity.ReadingContext)]
    [InlineData(ChiefTurnActivity.ReadingContext, ChiefTurnActivity.Thinking)]
    [InlineData(ChiefTurnActivity.Thinking, ChiefTurnActivity.Planning)]
    [InlineData(ChiefTurnActivity.Planning, ChiefTurnActivity.Delegating)]
    [InlineData(ChiefTurnActivity.Delegating, ChiefTurnActivity.AgentWorking)]
    [InlineData(ChiefTurnActivity.AgentWorking, ChiefTurnActivity.AwaitingReview)]
    [InlineData(ChiefTurnActivity.AwaitingReview, ChiefTurnActivity.Completed)]
    [InlineData(ChiefTurnActivity.Thinking, ChiefTurnActivity.Completed)]
    // Sem provider: received pode ir direto a blocked, de forma honesta.
    [InlineData(ChiefTurnActivity.Received, ChiefTurnActivity.Blocked)]
    // Qualquer fase de execução pode falhar.
    [InlineData(ChiefTurnActivity.ReadingContext, ChiefTurnActivity.Failed)]
    [InlineData(ChiefTurnActivity.AgentWorking, ChiefTurnActivity.Failed)]
    public void AllowsHonestForwardTransitions(ChiefTurnActivity from, ChiefTurnActivity to) =>
        Assert.True(ChiefTurnActivityState.CanTransition(from, to));

    [Theory]
    // Não se volta de planejar para ler contexto, nem se pula receber → concluir.
    [InlineData(ChiefTurnActivity.Planning, ChiefTurnActivity.ReadingContext)]
    [InlineData(ChiefTurnActivity.Received, ChiefTurnActivity.Completed)]
    [InlineData(ChiefTurnActivity.ReadingContext, ChiefTurnActivity.Completed)]
    [InlineData(ChiefTurnActivity.Thinking, ChiefTurnActivity.ReadingContext)]
    public void RejectsBackwardOrSkippingTransitions(ChiefTurnActivity from, ChiefTurnActivity to) =>
        Assert.False(ChiefTurnActivityState.CanTransition(from, to));
}
