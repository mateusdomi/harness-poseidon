using Harness.Modules.Governance.Ledger;
using Harness.SharedKernel.Auditing;

namespace Harness.UnitTests.Governance;

public sealed class LedgerReconciliationServiceTests
{
    [Fact]
    public void ReconcileValidChainReturnsValidResult()
    {
        var service = new LedgerReconciliationService();
        var tenantId = "tenant-1";
        var now = DateTimeOffset.UtcNow;
        var entries = Chain(tenantId, now, ("card.created", """{"card":"one"}"""),
            ("card.approved", """{"card":"one","approved":true}"""));

        var result = service.Reconcile(tenantId, entries);

        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal(2, result.TotalEntries);
        Assert.True(result.IsChainValid);
        Assert.Equal(entries[1].EventHash, result.LastValidHash);
        Assert.Empty(result.DiscrepancySequenceNumbers);
    }

    [Fact]
    public void ReconcileDetectsTamperedLastEntryAndSequenceGap()
    {
        var service = new LedgerReconciliationService();
        var entries = Chain(
            "tenant-1",
            DateTimeOffset.UtcNow,
            ("card.created", """{"card":"one"}"""),
            ("card.approved", """{"card":"one"}"""));
        entries[1] = entries[1] with
        {
            SequenceNumber = 3,
            EventHash = new string('F', 64),
        };

        var result = service.Reconcile("tenant-1", entries);

        Assert.False(result.IsChainValid);
        Assert.Equal(1, result.TamperedCount);
        Assert.Equal([3L], result.DiscrepancySequenceNumbers);
        Assert.Equal(entries[0].EventHash, result.LastValidHash);
    }

    [Fact]
    public void ExportAuditBundleJsonReturnsValidJson()
    {
        var service = new LedgerReconciliationService();
        var tenantId = "tenant-1";
        var entries = Chain(
            tenantId,
            DateTimeOffset.UtcNow,
            ("card.created", """{"turnId":"turn-1"}"""));
        entries.AddRange(Chain(
            "tenant-2",
            DateTimeOffset.UtcNow,
            ("card.created", """{"turnId":"foreign"}""")));

        var json = service.ExportAuditBundleJson(tenantId, entries);
        Assert.Contains("turn-1", json);
        Assert.Contains("card.created", json);
        Assert.Contains("reconciliation", json);
        Assert.DoesNotContain("foreign", json);
    }

    private static List<AuditLedgerEntry> Chain(
        string tenantId,
        DateTimeOffset occurredAt,
        params (string EventType, string Payload)[] events)
    {
        var result = new List<AuditLedgerEntry>();
        var previousHash = AuditChainHash.Genesis;
        for (var index = 0; index < events.Length; index++)
        {
            var sequence = index + 1L;
            var eventHash = AuditChainHash.Compute(
                previousHash,
                tenantId,
                sequence,
                events[index].EventType,
                events[index].Payload,
                occurredAt);
            result.Add(new AuditLedgerEntry(
                sequence,
                tenantId,
                events[index].EventType,
                events[index].Payload,
                previousHash,
                eventHash,
                occurredAt));
            previousHash = eventHash;
        }

        return result;
    }
}
