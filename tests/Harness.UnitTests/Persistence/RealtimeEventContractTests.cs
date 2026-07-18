using Harness.Persistence.Abstractions.Realtime;

namespace Harness.UnitTests.Persistence;

public sealed class RealtimeEventContractTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 18, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ValidCommandUsesCanonicalPayload()
    {
        var command = Command("{\"z\":1,\"nested\":{\"b\":2,\"a\":1}}");

        RealtimeEventContractValidator.Validate(command);

        Assert.Equal(
            "{\"nested\":{\"a\":1,\"b\":2},\"z\":1}",
            RealtimeEventContractValidator.CanonicalizePayload(command.PayloadJson));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("project:bad stream")]
    [InlineData(":missing-namespace")]
    public void InvalidStreamIsRejected(string stream)
    {
        Assert.Throws<ArgumentException>(
            () => RealtimeEventContractValidator.Validate(Command() with { Stream = stream }));
    }

    [Fact]
    public void NonObjectPayloadAndNonUtcTimestampAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => RealtimeEventContractValidator.Validate(Command("[]")));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RealtimeEventContractValidator.Validate(
                Command() with { OccurredAt = OccurredAt.ToOffset(TimeSpan.FromHours(-3)) }));
    }

    [Fact]
    public void ReadCursorCannotBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RealtimeEventContractValidator.ValidateRead(
                "project:01ARZ3NDEKTSV4RRFFQ69G5FAX",
                -1));
    }

    private static RealtimeEventAppendCommand Command(string payload = "{\"state\":\"running\"}") =>
        new(
            "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "project:01ARZ3NDEKTSV4RRFFQ69G5FAX",
            "task.stateChanged",
            payload,
            OccurredAt);
}
