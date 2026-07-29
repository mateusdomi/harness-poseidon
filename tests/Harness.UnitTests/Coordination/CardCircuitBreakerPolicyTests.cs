using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class CardCircuitBreakerPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoConsecutiveFailuresKeepTheCircuitClosed()
    {
        var circuit = CardCircuitSnapshot.Closed("card-1");
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now);
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(1));

        Assert.Equal(CardCircuitState.Closed, circuit.State);
        Assert.Equal(2, circuit.ConsecutiveFailures);
        Assert.True(circuit.IsDispatchable);
    }

    /// <summary>Gate da fase: três falhas consecutivas abrem o circuito.</summary>
    [Fact]
    public void ThreeConsecutiveFailuresOpenTheCircuit()
    {
        var circuit = CardCircuitSnapshot.Closed("card-1");
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now, "agent.timeout");
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(1), "agent.timeout");
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(2), "review.rejected");

        Assert.Equal(CardCircuitState.Open, circuit.State);
        Assert.Equal(3, circuit.ConsecutiveFailures);
        Assert.Equal(Now.AddMinutes(2), circuit.OpenedAt);
        Assert.Equal("review.rejected", circuit.LastFailureReasonCode);
        Assert.False(circuit.IsDispatchable);
    }

    [Fact]
    public void SuccessResetsTheStreakBecauseWhatOpensIsRepetitionNotHistory()
    {
        var circuit = CardCircuitSnapshot.Closed("card-1");
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now);
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(1));
        circuit = CardCircuitBreakerPolicy.RecordSuccess(circuit);
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(3));

        Assert.Equal(CardCircuitState.Closed, circuit.State);
        Assert.Equal(1, circuit.ConsecutiveFailures);
    }

    /// <summary>Gate da fase: só o replanejamento reabre — tempo não corrige enunciado.</summary>
    [Fact]
    public void OnlyReplanClosesAnOpenCircuit()
    {
        var circuit = Open("card-1");

        // Nem o tempo passando, nem novas falhas, nem uma nova rodada reabrem o circuito.
        var muchLater = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddDays(7));
        Assert.Equal(CardCircuitState.Open, muchLater.State);
        Assert.False(muchLater.IsDispatchable);

        var replanned = CardCircuitBreakerPolicy.Replan(muchLater);
        Assert.Equal(CardCircuitState.Closed, replanned.State);
        Assert.Equal(0, replanned.ConsecutiveFailures);
        Assert.Null(replanned.OpenedAt);
        Assert.True(replanned.IsDispatchable);
    }

    [Fact]
    public void FailuresOnAnOpenCircuitDoNotInflateTheCount()
    {
        var circuit = Open("card-1");
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(10));
        circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(20));

        Assert.Equal(CardCircuitBreakerPolicy.ConsecutiveFailureThreshold, circuit.ConsecutiveFailures);
    }

    [Fact]
    public void OpenCircuitsAreRemovedFromDispatchWithoutBeingRequeued()
    {
        var circuits = new Dictionary<string, CardCircuitSnapshot>(StringComparer.Ordinal)
        {
            ["card-open"] = Open("card-open"),
            ["card-warm"] = CardCircuitBreakerPolicy.RecordFailure(
                CardCircuitSnapshot.Closed("card-warm"), Now)
        };

        var dispatchable = CardCircuitBreakerPolicy.FilterDispatchable(
            ["card-open", "card-warm", "card-fresh"],
            circuits);

        Assert.Equal(["card-warm", "card-fresh"], dispatchable);
    }

    [Fact]
    public void ReplannedCardBecomesDispatchableAgain()
    {
        var circuits = new Dictionary<string, CardCircuitSnapshot>(StringComparer.Ordinal)
        {
            ["card-open"] = CardCircuitBreakerPolicy.Replan(Open("card-open"))
        };

        Assert.Equal(
            ["card-open"],
            CardCircuitBreakerPolicy.FilterDispatchable(["card-open"], circuits));
    }

    private static CardCircuitSnapshot Open(string cardId)
    {
        var circuit = CardCircuitSnapshot.Closed(cardId);
        for (var index = 0; index < CardCircuitBreakerPolicy.ConsecutiveFailureThreshold; index++)
        {
            circuit = CardCircuitBreakerPolicy.RecordFailure(circuit, Now.AddMinutes(index));
        }

        return circuit;
    }
}
