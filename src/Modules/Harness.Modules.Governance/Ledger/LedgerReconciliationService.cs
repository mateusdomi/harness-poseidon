using System.Text.Json;
using Harness.SharedKernel.Auditing;

namespace Harness.Modules.Governance.Ledger;

public sealed record AuditLedgerEntry(
    long SequenceNumber,
    string TenantId,
    string EventType,
    string PayloadJson,
    string PreviousHash,
    string EventHash,
    DateTimeOffset OccurredAt);

public sealed record LedgerReconciliationResult(
    string TenantId,
    long TotalEntries,
    bool IsChainValid,
    long TamperedCount,
    IReadOnlyList<long> DiscrepancySequenceNumbers,
    string LastValidHash,
    DateTimeOffset ReconciledAt);

public interface ILedgerReconciliationService
{
    LedgerReconciliationResult Reconcile(
        string tenantId,
        IReadOnlyList<AuditLedgerEntry> entries);

    string ExportAuditBundleJson(
        string tenantId,
        IReadOnlyList<AuditLedgerEntry> entries);
}

public sealed class LedgerReconciliationService : ILedgerReconciliationService
{
    private static readonly JsonSerializerOptions JsonIndentedOptions = new()
    {
        WriteIndented = true
    };

    public LedgerReconciliationResult Reconcile(
        string tenantId,
        IReadOnlyList<AuditLedgerEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(entries);

        var tenantEntries = entries
            .Where(e => string.Equals(e.TenantId, tenantId, StringComparison.Ordinal))
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        if (tenantEntries.Count == 0)
        {
            return new LedgerReconciliationResult(
                TenantId: tenantId,
                TotalEntries: 0,
                IsChainValid: true,
                TamperedCount: 0,
                DiscrepancySequenceNumbers: [],
                LastValidHash: AuditChainHash.Genesis,
                ReconciledAt: DateTimeOffset.UtcNow);
        }

        var discrepancies = new List<long>();
        var expectedPreviousHash = AuditChainHash.Genesis;
        var lastValidHash = AuditChainHash.Genesis;
        var expectedSequence = 1L;
        var chainIsValid = true;

        foreach (var entry in tenantEntries)
        {
            var computedHash = AuditChainHash.Compute(
                entry.PreviousHash,
                entry.TenantId,
                entry.SequenceNumber,
                entry.EventType,
                entry.PayloadJson,
                entry.OccurredAt);
            var entryIsValid =
                entry.SequenceNumber == expectedSequence &&
                string.Equals(entry.PreviousHash, expectedPreviousHash, StringComparison.Ordinal) &&
                string.Equals(entry.EventHash, computedHash, StringComparison.Ordinal);

            if (!entryIsValid)
            {
                discrepancies.Add(entry.SequenceNumber);
                chainIsValid = false;
            }
            else if (chainIsValid)
            {
                lastValidHash = entry.EventHash;
            }

            expectedPreviousHash = computedHash;
            expectedSequence++;
        }

        return new LedgerReconciliationResult(
            TenantId: tenantId,
            TotalEntries: tenantEntries.Count,
            IsChainValid: discrepancies.Count == 0,
            TamperedCount: discrepancies.Count,
            DiscrepancySequenceNumbers: discrepancies,
            LastValidHash: lastValidHash,
            ReconciledAt: DateTimeOffset.UtcNow);
    }

    public string ExportAuditBundleJson(
        string tenantId,
        IReadOnlyList<AuditLedgerEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(entries);

        var tenantEntries = entries
            .Where(e => string.Equals(e.TenantId, tenantId, StringComparison.Ordinal))
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        return JsonSerializer.Serialize(
            new
            {
                reconciliation = Reconcile(tenantId, tenantEntries),
                entries = tenantEntries,
            },
            JsonIndentedOptions);
    }
}
