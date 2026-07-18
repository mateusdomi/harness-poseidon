namespace Harness.Persistence.Abstractions.Foundation;

public sealed record FoundationStoreSnapshot(
    long Tenants,
    long Organizations,
    long Projects,
    long Users,
    long InboxMessages,
    long OutboxMessages,
    long LedgerEntries);
