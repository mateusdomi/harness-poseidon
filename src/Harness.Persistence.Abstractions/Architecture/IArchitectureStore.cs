namespace Harness.Persistence.Abstractions.Architecture;

/// <summary>
/// Store durável do Architecture Hub. Mantém um MODELO estruturado (elementos + relacionamentos),
/// VIEWS (seleções, nunca imagens), metadados de sistema, propostas e o histórico append-only de
/// versões. O modelo PROPOSTO e o VIGENTE ('proposed'/'implemented') coexistem nas mesmas tabelas,
/// separados pela coluna de estado (ARC-05). Multi-tenant por (tenant_id, id) como os stores irmãos.
/// </summary>
public interface IArchitectureStore
{
    // Elementos --------------------------------------------------------------------------------------
    Task CreateElementAsync(ArchitectureElementRecord element, CancellationToken cancellationToken = default);
    Task<ArchitectureElementRecord?> GetElementAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchitectureElementRecord>> ListElementsAsync(
        string tenantId, string? projectId, string state, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task ReplaceElementAsync(ArchitectureElementRecord element, CancellationToken cancellationToken = default);
    Task DeleteElementAsync(string tenantId, string id, CancellationToken cancellationToken = default);

    // Relacionamentos --------------------------------------------------------------------------------
    Task CreateRelationshipAsync(ArchitectureRelationshipRecord relationship, CancellationToken cancellationToken = default);
    Task<ArchitectureRelationshipRecord?> GetRelationshipAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchitectureRelationshipRecord>> ListRelationshipsAsync(
        string tenantId, string? projectId, string state, string? afterId, int limit,
        CancellationToken cancellationToken = default);
    Task ReplaceRelationshipAsync(ArchitectureRelationshipRecord relationship, CancellationToken cancellationToken = default);
    Task DeleteRelationshipAsync(string tenantId, string id, CancellationToken cancellationToken = default);

    // Views ------------------------------------------------------------------------------------------
    Task CreateViewAsync(ArchitectureViewRecord view, CancellationToken cancellationToken = default);
    Task<ArchitectureViewRecord?> GetViewAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchitectureViewRecord>> ListViewsAsync(
        string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default);

    // Metadados de sistema (ARC-02/03) ---------------------------------------------------------------
    Task UpsertSystemMetadataAsync(ArchitectureSystemMetadataRecord metadata, CancellationToken cancellationToken = default);
    Task<ArchitectureSystemMetadataRecord?> GetSystemMetadataAsync(string tenantId, string elementId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchitectureSystemMetadataRecord>> ListSystemMetadataAsync(
        string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default);

    // Propostas (ARC-05) -----------------------------------------------------------------------------
    Task CreateProposalAsync(ArchitectureProposalRecord proposal, CancellationToken cancellationToken = default);
    Task<ArchitectureProposalRecord?> GetProposalAsync(string tenantId, string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchitectureProposalRecord>> ListProposalsAsync(
        string tenantId, string? projectId, string? afterId, int limit, CancellationToken cancellationToken = default);
    Task ReplaceProposalAsync(ArchitectureProposalRecord proposal, CancellationToken cancellationToken = default);

    // Histórico append-only (ARC-01 versionamento / ARC-05 rollback) ---------------------------------
    Task AppendHistoryAsync(ArchitectureHistoryRecord entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchitectureHistoryRecord>> ListHistoryAsync(
        string tenantId, string entityId, int limit, CancellationToken cancellationToken = default);
}

public sealed record ArchitectureElementRecord(
    string TenantId,
    string Id,
    string? ProjectId,
    string Kind,
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Properties,
    string State,
    bool Locked,
    int Version,
    string? ProposalId,
    string? CounterpartId,
    string? ChangeKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ArchitectureRelationshipRecord(
    string TenantId,
    string Id,
    string? ProjectId,
    string SourceId,
    string TargetId,
    string Kind,
    IReadOnlyDictionary<string, string> Properties,
    string State,
    int Version,
    string? ProposalId,
    string? CounterpartId,
    string? ChangeKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ArchitectureViewRecord(
    string TenantId,
    string Id,
    string? ProjectId,
    string Name,
    string Description,
    string Notation,
    IReadOnlyList<string> ElementIds,
    IReadOnlyList<string> RelationshipIds,
    IReadOnlyList<string> FilterKinds,
    IReadOnlyList<string> FilterTags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ArchitectureDocumentLinkRecord(string Id, string Title, string Kind, string State);

public sealed record ArchitectureSystemMetadataRecord(
    string TenantId,
    string ElementId,
    string? ProjectId,
    string? Domain,
    IReadOnlyList<string> Capabilities,
    string? Owner,
    string Criticality,
    IReadOnlyList<string> TechStack,
    string? LifecycleStatus,
    decimal? CostMonthlyUsd,
    int IncidentCount,
    int? BusFactor,
    string? DuplicateOfId,
    string? RiskLevel,
    string? Sla,
    string? BackupPolicy,
    string? DrPolicy,
    DateTimeOffset? LastIncidentAt,
    bool Pii,
    bool Sensitive,
    string? Retention,
    IReadOnlyList<string> DataClasses,
    IReadOnlyList<ArchitectureDocumentLinkRecord> Documents,
    int AdrCount,
    IReadOnlyList<string> Risks,
    DateTimeOffset? LastReviewAt,
    string? ReviewConfidence,
    DateTimeOffset UpdatedAt);

public sealed record ArchitectureProposalRecord(
    string TenantId,
    string Id,
    string? ProjectId,
    string Title,
    string Status,
    string? Justification,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt);

public sealed record ArchitectureHistoryRecord(
    string TenantId,
    string Id,
    string EntityType,
    string EntityId,
    int Version,
    string SnapshotJson,
    string ChangeKind,
    string? Actor,
    string? Justification,
    DateTimeOffset OccurredAt);
