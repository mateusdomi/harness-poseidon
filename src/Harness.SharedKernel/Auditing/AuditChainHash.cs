using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harness.SharedKernel.Auditing;

public static class AuditChainHash
{
    public static string Genesis { get; } = new('0', 64);

    public static string Compute(
        string previousHash,
        string tenantId,
        long sequence,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAt)
    {
        var canonical = string.Join(
            "\n",
            previousHash,
            tenantId,
            sequence.ToString(CultureInfo.InvariantCulture),
            eventType,
            CanonicalizeJson(payloadJson),
            occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CanonicalizeJson(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                    property => property.Name,
                    StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
