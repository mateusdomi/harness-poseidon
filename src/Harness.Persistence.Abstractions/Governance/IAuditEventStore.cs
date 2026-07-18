namespace Harness.Persistence.Abstractions.Governance;

public interface IAuditEventStore
{
    Task<IReadOnlyList<AuditEventRecord>> ListAsync(AuditEventQuery query, CancellationToken cancellationToken = default);
    Task<AuditEventRecord?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<AuditIntegrityRecord> VerifyIntegrityAsync(string tenantId, CancellationToken cancellationToken = default);
}

public sealed record AuditEventQuery(
    string TenantId,
    string? AfterId,
    int Limit,
    string? ActorKind = null,
    string? ActorId = null,
    string? Action = null,
    string? TargetType = null,
    string? TargetId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record AuditEventRecord(
    string Id,
    string ActorKind,
    string? ActorId,
    string Action,
    string TargetType,
    string? TargetId,
    string? Detail,
    DateTimeOffset OccurredAt);

public sealed record AuditIntegrityRecord(
    bool Valid,
    long EntryCount,
    long LastSequence,
    string TailHash,
    long? FailedSequence);
