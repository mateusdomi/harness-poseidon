namespace Harness.ConcurrencyTests.Leases;

public sealed record SyntheticLeaseSnapshot(
    string ResourceId,
    string? Owner,
    long FencingToken,
    DateTimeOffset ExpiresAt,
    string? Value,
    int Version);
