using Harness.Modules.Governance.Ledger;

namespace Harness.UnitTests.Governance;

public sealed class LedgerReconciliationServiceTests
{
    [Fact]
    public void ReconcileValidChainReturnsValidResult()
    {
        var service = new LedgerReconciliationService();
        var tenantId = "tenant-1";
        var now = DateTimeOffset.UtcNow;

        var entries = new List<AuditLedgerEntry>
        {
            new(1, tenantId, "turn-1", "card.created", "hash1", "GENESIS", now),
            new(2, tenantId, "turn-2", "card.approved", "hash2", "a45dfc1d5338bfab5f8992e59e1cae389fa6e9fb5434d2847a95fb59b19e2467", now)
        };

        var result = service.Reconcile(tenantId, entries);

        Assert.NotNull(result);
        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal(2, result.TotalEntries);
    }

    [Fact]
    public void ExportAuditBundleJsonReturnsValidJson()
    {
        var service = new LedgerReconciliationService();
        var tenantId = "tenant-1";
        var entries = new List<AuditLedgerEntry>
        {
            new(1, tenantId, "turn-1", "card.created", "hash1", "GENESIS", DateTimeOffset.UtcNow)
        };

        var json = service.ExportAuditBundleJson(tenantId, entries);
        Assert.Contains("turn-1", json);
        Assert.Contains("card.created", json);
    }
}
