namespace Harness.Persistence.Abstractions.Foundation;

public sealed record ProjectProvisionCommand(
    string TenantId,
    string TenantName,
    string OrganizationId,
    string OrganizationName,
    string ProjectId,
    string ProjectName,
    string UserId,
    string UserDisplayName,
    string IdempotencyKey,
    string MessageHash,
    string LedgerEventId,
    string OutboxMessageId,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt);
