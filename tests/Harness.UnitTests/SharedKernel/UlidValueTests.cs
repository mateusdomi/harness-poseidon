using Harness.SharedKernel.Identifiers;

namespace Harness.UnitTests.SharedKernel;

public sealed class UlidValueTests
{
    private static readonly DateTimeOffset KnownTimestamp =
        DateTimeOffset.FromUnixTimeMilliseconds(1_469_922_850_259);

    [Fact]
    public void ParseAndFormatKnownCanonicalValue()
    {
        const string text = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

        var value = UlidValue.Parse(text);

        Assert.Equal(text, value.ToString());
        Assert.Equal(KnownTimestamp, value.Timestamp);
        Assert.Equal(16, value.ToByteArray().Length);
    }

    [Fact]
    public void ParseAcceptsLowercaseAndFormatsUppercase()
    {
        const string lowercase = "01arz3ndektsv4rrffq69g5fav";

        var value = UlidValue.Parse(lowercase);

        Assert.Equal(lowercase.ToUpperInvariant(), value.ToString());
    }

    [Fact]
    public void CreatePreservesTimestampAndRandomness()
    {
        byte[] randomness = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

        var value = UlidValue.Create(KnownTimestamp, randomness);
        var roundTrip = UlidValue.Parse(value.ToString());

        Assert.Equal(KnownTimestamp, value.Timestamp);
        Assert.Equal(value, roundTrip);
        Assert.Equal(randomness, value.ToByteArray()[6..]);
    }

    [Fact]
    public void LexicalOrderMatchesTimestampOrderForEqualRandomness()
    {
        var randomness = new byte[10];
        var earlier = UlidValue.Create(KnownTimestamp, randomness);
        var later = UlidValue.Create(KnownTimestamp.AddMilliseconds(1), randomness);

        Assert.True(earlier.CompareTo(later) < 0);
        Assert.True(string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA")]
    [InlineData("81ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAI")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAO")]
    public void TryParseRejectsNonCanonicalValues(string? text)
    {
        var parsed = UlidValue.TryParse(text, out var value);

        Assert.False(parsed);
        Assert.Equal(default, value);
    }

    [Fact]
    public void CreateRejectsInvalidRandomnessLength()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => UlidValue.Create(KnownTimestamp, new byte[9]));

        Assert.Equal("randomness", exception.ParamName);
    }

    [Fact]
    public void CreateRejectsTimestampBeforeUnixEpoch()
    {
        var timestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(-1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => UlidValue.Create(timestamp, new byte[10]));

        Assert.Equal("timestamp", exception.ParamName);
    }
}
