using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harness.Modules.Governance.Ledger;

public sealed record AuditLedgerEntry(
    long SequenceNumber,
    string TenantId,
    string TurnId,
    string Action,
    string ContentHash,
    string PreviousHash,
    DateTimeOffset Timestamp);

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
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(entries);

        var tenantEntries = entries
            .Where(e => string.Equals(e.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
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
                LastValidHash: "GENESIS",
                ReconciledAt: DateTimeOffset.UtcNow);
        }

        var discrepancies = new List<long>();
        var previousHash = "GENESIS";

        foreach (var entry in tenantEntries)
        {
            if (!string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal))
            {
                discrepancies.Add(entry.SequenceNumber);
            }

            // Recalcula o hash do elo da corrente
            var payload = $"{entry.SequenceNumber}:{entry.TenantId}:{entry.TurnId}:{entry.Action}:{entry.ContentHash}:{entry.PreviousHash}";
            var computedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

            previousHash = computedHash;
        }

        return new LedgerReconciliationResult(
            TenantId: tenantId,
            TotalEntries: tenantEntries.Count,
            IsChainValid: discrepancies.Count == 0,
            TamperedCount: discrepancies.Count,
            DiscrepancySequenceNumbers: discrepancies,
            LastValidHash: previousHash,
            ReconciledAt: DateTimeOffset.UtcNow);
    }

    public string ExportAuditBundleJson(
        string tenantId,
        IReadOnlyList<AuditLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(entries);

        var tenantEntries = entries
            .Where(e => string.Equals(e.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        return JsonSerializer.Serialize(tenantEntries, JsonIndentedOptions);
    }
}
