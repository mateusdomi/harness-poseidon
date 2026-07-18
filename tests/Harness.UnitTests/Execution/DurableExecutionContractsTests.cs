using Harness.Persistence.Abstractions.DurableExecution;

namespace Harness.UnitTests.Execution;

public sealed class DurableExecutionContractsTests
{
    private const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string ExecutionId = "01ARZ3NDEKTSV4RRFFQ69G5FB6";

    [Fact]
    public void StateMachineAllowsOnlyDeclaredTransitions()
    {
        var allowed = new HashSet<(DurableExecutionState From, DurableExecutionState To)>
        {
            (DurableExecutionState.Ready, DurableExecutionState.Running),
            (DurableExecutionState.Ready, DurableExecutionState.Paused),
            (DurableExecutionState.Ready, DurableExecutionState.Cancelled),
            (DurableExecutionState.Running, DurableExecutionState.Paused),
            (DurableExecutionState.Running, DurableExecutionState.WaitingForRetry),
            (DurableExecutionState.Running, DurableExecutionState.WaitingForSignal),
            (DurableExecutionState.Running, DurableExecutionState.Completed),
            (DurableExecutionState.Running, DurableExecutionState.Cancelled),
            (DurableExecutionState.Running, DurableExecutionState.DeadLetter),
            (DurableExecutionState.Paused, DurableExecutionState.Ready),
            (DurableExecutionState.Paused, DurableExecutionState.Cancelled),
            (DurableExecutionState.WaitingForRetry, DurableExecutionState.Ready),
            (DurableExecutionState.WaitingForRetry, DurableExecutionState.Paused),
            (DurableExecutionState.WaitingForRetry, DurableExecutionState.Cancelled),
            (DurableExecutionState.WaitingForRetry, DurableExecutionState.DeadLetter),
            (DurableExecutionState.WaitingForSignal, DurableExecutionState.Ready),
            (DurableExecutionState.WaitingForSignal, DurableExecutionState.Paused),
            (DurableExecutionState.WaitingForSignal, DurableExecutionState.Cancelled),
            (DurableExecutionState.WaitingForSignal, DurableExecutionState.DeadLetter),
        };

        foreach (var from in Enum.GetValues<DurableExecutionState>())
        {
            foreach (var to in Enum.GetValues<DurableExecutionState>())
            {
                Assert.Equal(
                    allowed.Contains((from, to)),
                    DurableExecutionStateMachine.CanTransition(from, to));
            }
        }
    }

    [Theory]
    [InlineData(DurableExecutionState.Completed)]
    [InlineData(DurableExecutionState.Cancelled)]
    [InlineData(DurableExecutionState.DeadLetter)]
    public void TerminalStatesHaveNoOutgoingTransition(DurableExecutionState terminal)
    {
        Assert.True(DurableExecutionStateMachine.IsTerminal(terminal));
        Assert.All(
            Enum.GetValues<DurableExecutionState>(),
            target => Assert.False(DurableExecutionStateMachine.CanTransition(terminal, target)));
    }

    [Fact]
    public void RetryBackoffIsDeterministicAndCapped()
    {
        var policy = new DurableRetryPolicy(
            maxAttempts: 6,
            initialDelay: TimeSpan.FromSeconds(2),
            multiplier: 2.5m,
            maximumDelay: TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayAfterFailure(1));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayAfterFailure(2));
        Assert.Equal(TimeSpan.FromSeconds(12.5), policy.DelayAfterFailure(3));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayAfterFailure(4));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayAfterFailure(200));
    }

    [Fact]
    public void RetryPolicyRejectsUnsafeBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DurableRetryPolicy(0, TimeSpan.Zero, 1m, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DurableRetryPolicy(1, TimeSpan.FromSeconds(-1), 1m, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DurableRetryPolicy(1, TimeSpan.Zero, 0.9m, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DurableRetryPolicy(1, TimeSpan.FromSeconds(2), 1m, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void StateCodecsRoundTripEveryDeclaredState()
    {
        Assert.All(Enum.GetValues<DurableExecutionState>(), state =>
            Assert.Equal(state, DurableExecutionStateCodec.Parse(DurableExecutionStateCodec.ToStorage(state))));
        Assert.All(Enum.GetValues<DurableAttemptState>(), state =>
            Assert.Equal(state, DurableAttemptStateCodec.Parse(DurableAttemptStateCodec.ToStorage(state))));
    }

    [Fact]
    public void ValidatorAcceptsCanonicalStartAndRejectsMalformedPayload()
    {
        var request = new DurableExecutionStartRequest(
            TenantId,
            ProjectId,
            ExecutionId,
            "{\"kind\":\"test\"}",
            new DurableRetryPolicy(3, TimeSpan.FromSeconds(1), 2m, TimeSpan.FromSeconds(30)),
            new DateTimeOffset(2026, 7, 18, 14, 15, 0, TimeSpan.Zero),
            "execution:start");

        DurableExecutionContractValidator.Validate(request);
        Assert.Equal(DurableCommandHash.Compute(request), DurableCommandHash.Compute(request));
        Assert.Throws<ArgumentException>(() =>
            DurableExecutionContractValidator.Validate(request with { PayloadJson = "{broken}" }));
        Assert.Throws<ArgumentException>(() =>
            DurableExecutionContractValidator.Validate(request with { ExecutionId = "not-ulid" }));
        Assert.Throws<ArgumentException>(() =>
            DurableExecutionContractValidator.Validate(request with
            {
                RetryPolicy = new DurableRetryPolicy(
                    3,
                    TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1),
                    2m,
                    TimeSpan.FromSeconds(30)),
            }));
    }

    [Fact]
    public void ValidatorBoundsLeaseDurationAndFencingToken()
    {
        var command = new DurableLeaseCommand(
            TenantId,
            ExecutionId,
            "01ARZ3NDEKTSV4RRFFQ69G5FB8",
            "owner-a",
            1,
            new DateTimeOffset(2026, 7, 18, 14, 15, 0, TimeSpan.Zero),
            "lease:renew");

        DurableExecutionContractValidator.Validate(command);
        DurableExecutionContractValidator.ValidateLeaseDuration(TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DurableExecutionContractValidator.Validate(command with { FencingToken = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DurableExecutionContractValidator.ValidateLeaseDuration(TimeSpan.FromDays(2)));
    }
}
