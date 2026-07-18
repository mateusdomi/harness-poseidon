using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Harness.SharedKernel.Identifiers;

public readonly record struct UlidValue(ulong High, ulong Low) : IComparable<UlidValue>
{
    private const int ByteLength = 16;
    private const int RandomnessLength = 10;
    private const int TextLength = 26;
    private const long MaximumTimestamp = 0xFFFFFFFFFFFF;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public bool IsEmpty => High == 0 && Low == 0;

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds((long)(High >> 16));

    public static UlidValue New(DateTimeOffset timestamp)
    {
        Span<byte> randomness = stackalloc byte[RandomnessLength];
        RandomNumberGenerator.Fill(randomness);
        return Create(timestamp, randomness);
    }

    public static UlidValue Create(DateTimeOffset timestamp, ReadOnlySpan<byte> randomness)
    {
        if (randomness.Length != RandomnessLength)
        {
            throw new ArgumentException($"ULID randomness must contain {RandomnessLength} bytes.", nameof(randomness));
        }

        var unixMilliseconds = timestamp.ToUnixTimeMilliseconds();
        if (unixMilliseconds is < 0 or > MaximumTimestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "ULID timestamp must fit in 48 unsigned bits.");
        }

        Span<byte> bytes = stackalloc byte[ByteLength];
        bytes[0] = (byte)(unixMilliseconds >> 40);
        bytes[1] = (byte)(unixMilliseconds >> 32);
        bytes[2] = (byte)(unixMilliseconds >> 24);
        bytes[3] = (byte)(unixMilliseconds >> 16);
        bytes[4] = (byte)(unixMilliseconds >> 8);
        bytes[5] = (byte)unixMilliseconds;
        randomness.CopyTo(bytes[6..]);

        return FromBytes(bytes);
    }

    public static UlidValue Parse(string text)
    {
        if (!TryParse(text, out var value))
        {
            throw new FormatException("The value is not a canonical ULID.");
        }

        return value;
    }

    public static bool TryParse(string? text, out UlidValue value)
    {
        value = default;

        if (text is null || text.Length != TextLength)
        {
            return false;
        }

        var accumulator = BigInteger.Zero;
        foreach (var character in text)
        {
            var digit = Decode(character);
            if (digit < 0)
            {
                return false;
            }

            accumulator = (accumulator << 5) | digit;
        }

        var encoded = accumulator.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (encoded.Length > ByteLength)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[ByteLength];
        encoded.CopyTo(bytes[(ByteLength - encoded.Length)..]);
        value = FromBytes(bytes);
        return true;
    }

    public byte[] ToByteArray()
    {
        var bytes = new byte[ByteLength];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(sizeof(ulong)), Low);
        return bytes;
    }

    public int CompareTo(UlidValue other)
    {
        var highComparison = High.CompareTo(other.High);
        return highComparison != 0 ? highComparison : Low.CompareTo(other.Low);
    }

    public static bool operator <(UlidValue left, UlidValue right) => left.CompareTo(right) < 0;

    public static bool operator <=(UlidValue left, UlidValue right) => left.CompareTo(right) <= 0;

    public static bool operator >(UlidValue left, UlidValue right) => left.CompareTo(right) > 0;

    public static bool operator >=(UlidValue left, UlidValue right) => left.CompareTo(right) >= 0;

    public override string ToString()
    {
        var accumulator = new BigInteger(ToByteArray(), isUnsigned: true, isBigEndian: true);
        Span<char> characters = stackalloc char[TextLength];

        for (var index = TextLength - 1; index >= 0; index--)
        {
            accumulator = BigInteger.DivRem(accumulator, 32, out var remainder);
            characters[index] = Alphabet[(int)remainder];
        }

        return new string(characters);
    }

    private static UlidValue FromBytes(ReadOnlySpan<byte> bytes)
    {
        return new UlidValue(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[sizeof(ulong)..]));
    }

    private static int Decode(char character)
    {
        var normalized = char.ToUpperInvariant(character);
        return Alphabet.IndexOf(normalized, StringComparison.Ordinal);
    }
}
