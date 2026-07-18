using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Harness.Persistence.Abstractions.Foundation;

public static class AuditLedgerHash
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
            payloadJson,
            occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
