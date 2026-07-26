namespace Harness.Persistence.Abstractions.Governance;

public interface IAuditEventStore
{
    Task<IReadOnlyList<AuditEventRecord>> ListAsync(AuditEventQuery query, CancellationToken cancellationToken = default);
    Task<AuditEventRecord?> GetAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<AuditIntegrityRecord> VerifyIntegrityAsync(string tenantId, CancellationToken cancellationToken = default);
    Task<AuditEventRecord> AppendAsync(AuditEventAppendCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fase 12: leitura CRUA da cadeia (sequência, tipo, payload e hashes como persistidos),
    /// em ordem de sequência, para reconciliação e exportação auditável fora do store. Nunca
    /// recalcula nem corrige nada — devolve o fato como está no banco.
    /// </summary>
    Task<IReadOnlyList<AuditChainRowRecord>> ListChainAsync(
        string tenantId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Linha crua do `audit_ledger`, exatamente como persistida.</summary>
public sealed record AuditChainRowRecord(
    long Sequence,
    string EventType,
    string PayloadJson,
    string PreviousHash,
    string EventHash,
    DateTimeOffset OccurredAt);

public sealed record AuditEventAppendCommand(
    string TenantId,
    string ActorKind,
    string? ActorId,
    string Action,
    string TargetType,
    string? TargetId,
    string? Detail,
    DateTimeOffset OccurredAt);

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
