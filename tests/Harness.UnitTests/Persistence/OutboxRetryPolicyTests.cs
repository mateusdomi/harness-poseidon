using Harness.Persistence.Abstractions.Messaging;

namespace Harness.UnitTests.Persistence;

public sealed class OutboxRetryPolicyTests
{
    [Fact]
    public void BackoffIsDeterministicAndCapped()
    {
        var policy = new OutboxRetryPolicy(
            5,
            TimeSpan.FromSeconds(1),
            2m,
            TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayAfterFailure(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayAfterFailure(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayAfterFailure(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayAfterFailure(4));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayAfterFailure(50));
    }

    [Fact]
    public void InvalidPolicyAndAttemptAreRejected()
    {
        var invalid = new OutboxRetryPolicy(
            0,
            TimeSpan.Zero,
            0m,
            TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);

        var valid = new OutboxRetryPolicy(
            3,
            TimeSpan.FromSeconds(1),
            2m,
            TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => valid.DelayAfterFailure(0));
    }
}
