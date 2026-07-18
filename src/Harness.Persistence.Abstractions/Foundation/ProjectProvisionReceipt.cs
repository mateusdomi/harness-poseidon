namespace Harness.Persistence.Abstractions.Foundation;

public sealed record ProjectProvisionReceipt(
    string ProjectId,
    long LedgerSequence,
    string LedgerHash,
    string OutboxMessageId,
    bool Replay);
