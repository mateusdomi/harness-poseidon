using Harness.SharedKernel.Time;

namespace Harness.UnitTests.SharedKernel;

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNowUsesUtcOffset()
    {
        var before = DateTimeOffset.UtcNow;
        var observed = SystemClock.Instance.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, observed.Offset);
        Assert.InRange(observed, before, after);
    }
}
