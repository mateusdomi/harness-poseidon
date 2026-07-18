namespace Harness.Persistence.Postgres;

public sealed record PostgresWorkItemLease(
    string WorkItemId,
    string OwnerId,
    long FencingToken,
    DateTimeOffset ExpiresAt,
    long Version);
