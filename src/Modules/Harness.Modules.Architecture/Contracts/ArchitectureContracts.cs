namespace Harness.Modules.Architecture.Contracts;

// Architecture Hub (Solutions Architect). O Hub mantém um MODELO estruturado (elementos +
// relacionamentos), nunca imagens; diagramas são VIEWS. Os contratos abaixo são READ-MODELS/DTOs.
// Números e sinais derivam ESTRITAMENTE de fatos gravados; nada é inventado.

// ---------------------------------------------------------------------------------------------------
// ARC-01 — Modelo estruturado (elementos, relacionamentos, views, dependências)
// ---------------------------------------------------------------------------------------------------

public sealed record ArchitectureElementContract(
    string Id,
    string? ProjectId,
    string Kind,
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Properties,
    string State,
    bool Locked,
    int Version);

public sealed record ArchitectureRelationshipContract(
    string Id,
    string? ProjectId,
    string SourceId,
    string TargetId,
    string Kind,
    IReadOnlyDictionary<string, string> Properties,
    string State,
    int Version);

public sealed record ArchitectureElementPageContract(
    int Total, string? NextCursor, IReadOnlyList<ArchitectureElementContract> Elements);

public sealed record ArchitectureRelationshipListContract(
    int Total, IReadOnlyList<ArchitectureRelationshipContract> Relationships);

/// <summary>Uma view é uma SELEÇÃO nomeada (C4/ArchiMate) sobre o modelo — nunca uma imagem.</summary>
public sealed record ArchitectureViewContract(
    string Id,
    string? ProjectId,
    string Name,
    string Description,
    string Notation,
    IReadOnlyList<ArchitectureElementContract> Elements,
    IReadOnlyList<ArchitectureRelationshipContract> Relationships);

public sealed record ArchitectureViewSummaryContract(
    string Id, string? ProjectId, string Name, string Notation, int ElementCount);

public sealed record ArchitectureViewListContract(
    int Total, IReadOnlyList<ArchitectureViewSummaryContract> Views);

/// <summary>Resposta de "quem depende de X": dependentes diretos e transitivos (ARC-01).</summary>
public sealed record ArchitectureDependentContract(
    string ElementId, string Name, string Kind, string Via, bool Direct);

public sealed record ArchitectureDependencyQueryContract(
    string ElementId,
    string Name,
    int DirectCount,
    int TransitiveCount,
    IReadOnlyList<ArchitectureDependentContract> Dependents);

// ---------------------------------------------------------------------------------------------------
// ARC-02 — Mapa Corporativo de Sistemas
// ---------------------------------------------------------------------------------------------------

public sealed record SystemCatalogEntryContract(
    string Id,
    string Name,
    string Description,
    string? Domain,
    IReadOnlyList<string> Capabilities,
    string? Owner,
    string Criticality,
    string? LifecycleStatus,
    IReadOnlyList<string> HeatSignals,
    int HeatScore);

public sealed record SystemCatalogContract(
    int Total, string? NextCursor, IReadOnlyList<SystemCatalogEntryContract> Systems);

public sealed record DomainMapNodeContract(string Domain, int SystemCount, IReadOnlyList<string> SystemIds);
public sealed record DomainMapContract(int Total, IReadOnlyList<DomainMapNodeContract> Domains);

public sealed record CapabilityMapNodeContract(string Capability, int SystemCount, IReadOnlyList<string> SystemIds);
public sealed record CapabilityMapContract(int Total, IReadOnlyList<CapabilityMapNodeContract> Capabilities);

public sealed record IntegrationEdgeContract(
    string SourceId, string SourceName, string TargetId, string TargetName, string Kind);
public sealed record IntegrationGraphContract(
    int SystemCount, int EdgeCount, IReadOnlyList<IntegrationEdgeContract> Edges);

/// <summary>Um sinal de heatmap DERIVADO de um metadado gravado (ARC-02). Nada é inventado.</summary>
public sealed record HeatSignalContract(string Code, string Detail);
public sealed record SystemHeatmapEntryContract(
    string Id, string Name, string? Domain, int Score, IReadOnlyList<HeatSignalContract> Signals);
public sealed record SystemHeatmapContract(
    int Total,
    IReadOnlyDictionary<string, int> SignalTotals,
    IReadOnlyList<SystemHeatmapEntryContract> Systems);

// ---------------------------------------------------------------------------------------------------
// ARC-03 — Sistema 360
// ---------------------------------------------------------------------------------------------------

public sealed record System360BusinessContract(
    string Name, string Description, string? Domain, IReadOnlyList<string> Capabilities,
    string Criticality, string? Owner);

public sealed record System360TechnologyContract(
    IReadOnlyList<string> TechStack, string? LifecycleStatus,
    IReadOnlyList<ArchitectureElementContract> Containers);

public sealed record System360IntegrationsContract(
    IReadOnlyList<IntegrationEdgeContract> Outgoing, IReadOnlyList<IntegrationEdgeContract> Incoming);

public sealed record System360OperationContract(
    string? Sla, int IncidentCount, DateTimeOffset? LastIncidentAt,
    string? BackupPolicy, string? DrPolicy, decimal? CostMonthlyUsd);

public sealed record System360DataContract(
    bool Pii, bool Sensitive, string? Retention, IReadOnlyList<string> DataClasses);

public sealed record System360GovernanceContract(
    IReadOnlyList<ArchDocumentRefContract> Documents, int AdrCount, IReadOnlyList<string> Risks,
    DateTimeOffset? LastReviewAt, string? ReviewConfidence, int? BusFactor);

public sealed record ArchDocumentRefContract(string Id, string Title, string Kind, string State);

public sealed record System360Contract(
    string SystemId,
    System360BusinessContract Business,
    System360TechnologyContract Technology,
    System360IntegrationsContract Integrations,
    System360OperationContract Operation,
    System360DataContract Data,
    System360GovernanceContract Governance);

// ---------------------------------------------------------------------------------------------------
// ARC-05 — Edição humana da arquitetura proposta (diff, lock, impacto, aplicar, rollback)
// ---------------------------------------------------------------------------------------------------

public sealed record ArchitectureProposalContract(
    string Id, string? ProjectId, string Title, string Status,
    string? Justification, DateTimeOffset CreatedAt, DateTimeOffset? AppliedAt);

public sealed record ArchitectureProposalListContract(
    int Total, IReadOnlyList<ArchitectureProposalContract> Proposals);

/// <summary>Uma diferença entre o modelo proposto e o vigente (ARC-05).</summary>
public sealed record ArchitectureChangeContract(
    string EntityType,
    string ChangeKind,
    string? CounterpartId,
    string ProposedId,
    string Label,
    bool TargetsLockedElement,
    bool Critical,
    IReadOnlyList<string> FieldChanges);

public sealed record ArchitectureDiffContract(
    string ProposalId,
    int Added,
    int Modified,
    int Removed,
    int BlockedByLock,
    bool RequiresJustification,
    IReadOnlyList<ArchitectureChangeContract> Changes);

public sealed record ArchitectureImpactContract(
    string ProposalId,
    IReadOnlyList<ArchitectureDependentContract> AffectedDependents,
    IReadOnlyList<ArchDocumentRefContract> AffectedDocuments,
    IReadOnlyList<ArchitectureChangeContract> BlockedChanges,
    bool RequiresJustification);

public sealed record ArchitectureApplyResultContract(
    string ProposalId,
    string Status,
    int Applied,
    int SkippedLocked,
    IReadOnlyList<ArchitectureChangeContract> AppliedChanges,
    IReadOnlyList<ArchitectureChangeContract> SkippedChanges,
    IReadOnlyList<ArchDocumentRefContract> DraftedDocuments);

public sealed record ArchitectureHistoryEntryContract(
    string Id, string EntityType, string EntityId, int Version, string ChangeKind,
    string? Actor, string? Justification, DateTimeOffset OccurredAt);

public sealed record ArchitectureHistoryContract(
    string EntityId, IReadOnlyList<ArchitectureHistoryEntryContract> History);
