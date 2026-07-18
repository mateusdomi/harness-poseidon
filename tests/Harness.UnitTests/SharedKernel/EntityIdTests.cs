using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.UnitTests.SharedKernel;

public sealed class EntityIdTests
{
    private sealed class ProjectTag;

    private sealed class TaskTag;

    [Fact]
    public void TagsCreateDistinctIdentifierTypes()
    {
        Assert.NotEqual(typeof(EntityId<ProjectTag>), typeof(EntityId<TaskTag>));
    }

    [Fact]
    public void NewUsesClockTimestampAndRoundTrips()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_721_234_567_890);
        var id = EntityId<ProjectTag>.New(new StubClock(now));

        var parsed = EntityId<ProjectTag>.Parse(id.ToString());

        Assert.False(id.IsEmpty);
        Assert.Equal(now, id.Value.Timestamp);
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void TryParseReturnsFalseForInvalidValue()
    {
        var parsed = EntityId<ProjectTag>.TryParse("not-an-id", out var id);

        Assert.False(parsed);
        Assert.True(id.IsEmpty);
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
