using Harness.Persistence.Abstractions.Foundation;

namespace Harness.UnitTests.Persistence;

public sealed class AuditLedgerHashTests
{
    [Fact]
    public void HashIsStableAcrossJsonbRepresentationChanges()
    {
        var occurredAt = new DateTimeOffset(2026, 7, 18, 15, 0, 0, TimeSpan.Zero);
        var compact = "{\"z\":1,\"nested\":{\"b\":true,\"a\":null},\"items\":[2,1]}";
        var normalized = "{\"z\": 1, \"items\": [2, 1], \"nested\": {\"a\": null, \"b\": true}}";

        var first = AuditLedgerHash.Compute(
            AuditLedgerHash.Genesis,
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            1,
            "audit.test",
            compact,
            occurredAt);
        var second = AuditLedgerHash.Compute(
            AuditLedgerHash.Genesis,
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            1,
            "audit.test",
            normalized,
            occurredAt);

        Assert.Equal(first, second);
        Assert.Equal(
            "{\"items\":[2,1],\"nested\":{\"a\":null,\"b\":true},\"z\":1}",
            AuditLedgerHash.CanonicalizeJson(compact));
    }
}
