using Harness.SharedKernel.Auditing;

namespace Harness.Persistence.Abstractions.Foundation;

public static class AuditLedgerHash
{
    public static string Genesis => AuditChainHash.Genesis;

    public static string Compute(
        string previousHash,
        string tenantId,
        long sequence,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAt)
        => AuditChainHash.Compute(
            previousHash,
            tenantId,
            sequence,
            eventType,
            payloadJson,
            occurredAt);

    public static string CanonicalizeJson(string payloadJson) =>
        AuditChainHash.CanonicalizeJson(payloadJson);
}
