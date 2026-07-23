namespace Harness.Modules.Architecture.Application;

/// <summary>
/// Fatos PUROS do modelo arquitetural já coletados do store — sem IO. É a entrada única dos
/// projetores/analisadores ARC-01/02/03/05. Cada campo mapeia para um registro persistido; nada é
/// inventado. O Host materializa este input a partir dos stores e delega toda a lógica aqui.
/// </summary>
public sealed record ArchitectureModelInput(
    IReadOnlyList<ArchElement> Elements,
    IReadOnlyList<ArchRelationship> Relationships,
    IReadOnlyList<ArchSystemMetadata> Systems);

/// <summary>
/// Um elemento do modelo (ARC-01). O mesmo elemento é REUSADO por várias views; nunca é copiado.
/// <see cref="State"/> separa a arquitetura vigente ('implemented') da proposta ('proposed').
/// <see cref="Locked"/> impede que uma mudança proposta por agente o sobrescreva (ARC-05).
/// </summary>
public sealed record ArchElement(
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
    string? ChangeKind);

/// <summary>Um relacionamento direcionado entre dois elementos (ARC-01).</summary>
public sealed record ArchRelationship(
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
    string? ChangeKind);

/// <summary>
/// Metadados de um SISTEMA (elemento de kind=system) usados pelo Mapa Corporativo (ARC-02) e pelo
/// Sistema 360 (ARC-03). Todos os campos são registrados explicitamente; heatmaps e seções 360 são
/// DERIVADOS estritamente destes fatos — nada é fabricado.
/// </summary>
public sealed record ArchSystemMetadata(
    string ElementId,
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
    IReadOnlyList<ArchDocumentLink> Documents,
    int AdrCount,
    IReadOnlyList<string> Risks,
    DateTimeOffset? LastReviewAt,
    string? ReviewConfidence);

/// <summary>Um documento associado a um sistema (governança 360). 'draft' após impacto (ARC-05).</summary>
public sealed record ArchDocumentLink(string Id, string Title, string Kind, string State);
