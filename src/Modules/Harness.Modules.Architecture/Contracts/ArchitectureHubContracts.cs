namespace Harness.Modules.Architecture.Contracts;

// Extensões do Architecture Hub (ARC-06/07/08/10). Como os contratos ARC-01/05, são READ-MODELS/DTOs:
// números e sinais derivam ESTRITAMENTE de fatos gravados; nada é inventado.

// ---------------------------------------------------------------------------------------------------
// ARC-06 — Descoberta de sistemas existentes
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// Uma informação DESCOBERTA sobre um sistema existente, a partir de uma fonte (repo/docs/OpenAPI/...).
/// Cada informação carrega SEMPRE <see cref="Confidence"/> + <see cref="Evidence"/> +
/// <see cref="PendingQuestions"/>; nada é afirmado sem confiança e evidência explícitas (ARC-06).
/// </summary>
public sealed record DiscoveryContract(
    string Id,
    string? ProjectId,
    string? SystemId,
    string SubjectName,
    string SourceKind,
    string Field,
    string Value,
    string Confidence,
    string Evidence,
    IReadOnlyList<string> PendingQuestions,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DiscoveryListContract(
    int Total, string? NextCursor, IReadOnlyList<DiscoveryContract> Discoveries);

/// <summary>
/// Rollup determinístico das descobertas de UM sujeito (sistema ou candidato). A confiança geral é a
/// MAIS CONSERVADORA entre as informações ainda abertas (o elo mais fraco), e as perguntas pendentes
/// são agregadas para o arquiteto resolver antes de promover a descoberta a fato do modelo.
/// </summary>
public sealed record DiscoverySystemSummaryContract(
    string? SystemId,
    string SubjectName,
    int DiscoveryCount,
    int OpenCount,
    int ConfirmedCount,
    string OverallConfidence,
    IReadOnlyDictionary<string, int> BySource,
    IReadOnlyList<string> PendingQuestions);

public sealed record DiscoverySummaryListContract(
    int Total, IReadOnlyList<DiscoverySystemSummaryContract> Subjects);

// ---------------------------------------------------------------------------------------------------
// ARC-07 — Insights & Racionalização (proposta, nunca ação)
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// Um insight de racionalização. O agente SUGERE com impacto, NUNCA decide sozinho: é uma recomendação
/// (<see cref="Classification"/>) apoiada em <see cref="Evidence"/> gravada e no <see cref="Impact"/>
/// concreto (dependentes afetados), para um humano decidir. Manter/Modernizar/Consolidar/Substituir/
/// Desativar/Investigar.
/// </summary>
public sealed record RationalizationInsightContract(
    string SystemId,
    string SystemName,
    string Category,
    string Classification,
    string Rationale,
    string Evidence,
    int AffectedDependentCount,
    IReadOnlyList<string> RelatedSystemIds);

public sealed record RationalizationReportContract(
    int SystemCount,
    int InsightCount,
    IReadOnlyDictionary<string, int> ByClassification,
    IReadOnlyList<RationalizationInsightContract> Insights);

// ---------------------------------------------------------------------------------------------------
// ARC-08 — Padrões & Decisões (ADRs corporativos + padrões reutilizáveis)
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// Um item do acervo de padrões &amp; decisões: um ADR corporativo (kind='adr') ou um padrão
/// reutilizável (kind='pattern'). Reusa Documents/ADR via <see cref="DocumentId"/> quando fizer sentido.
/// </summary>
public sealed record ArchitecturePatternContract(
    string Id,
    string? ProjectId,
    string Kind,
    string Title,
    string Status,
    string Context,
    string Body,
    string? Problem,
    string Consequences,
    IReadOnlyList<string> Tags,
    string? SupersedesId,
    string? DocumentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ArchitecturePatternListContract(
    int Total, string? NextCursor, IReadOnlyList<ArchitecturePatternContract> Items);

// ---------------------------------------------------------------------------------------------------
// ARC-10 — Integração Delivery↔Architecture
// ---------------------------------------------------------------------------------------------------

/// <summary>Um item de um snapshot arquitetural congelado (baseline / as-built). Reuso de elementos.</summary>
public sealed record ArchitectureSnapshotElement(string Id, string Kind, string Name);

/// <summary>Uma aresta de um snapshot arquitetural congelado.</summary>
public sealed record ArchitectureSnapshotEdge(string SourceId, string TargetId, string Kind);

/// <summary>Foto imutável do modelo (elementos + relações) usada como baseline/as-built (ARC-10).</summary>
public sealed record ArchitectureModelSnapshot(
    IReadOnlyList<ArchitectureSnapshotElement> Elements,
    IReadOnlyList<ArchitectureSnapshotEdge> Edges);

/// <summary>
/// A baseline arquitetural de uma entrega. Nasce da arquitetura APROVADA (proposta aplicada) e depois
/// recebe o AS-IS de produção. No encerramento, compara-se proposta × implementação (ARC-10).
/// </summary>
public sealed record ArchitectureBaselineContract(
    string Id,
    string ProjectId,
    string Status,
    string Title,
    string? ProposalId,
    int BaselineElementCount,
    bool HasAsBuilt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ArchitectureBaselineListContract(
    int Total, IReadOnlyList<ArchitectureBaselineContract> Baselines);

/// <summary>Uma diferença entre a arquitetura proposta (baseline) e a implementada (as-built).</summary>
public sealed record BaselineDriftEntryContract(
    string ElementId, string Name, string Kind, string Drift);

/// <summary>
/// Comparação proposta × implementação de uma baseline (encerramento). Deriva estritamente dos dois
/// snapshots gravados: elementos/arestas ausentes, adicionados fora do plano, ou casados sem desvio.
/// </summary>
public sealed record BaselineComparisonContract(
    string BaselineId,
    int Matched,
    int Missing,
    int Unplanned,
    int EdgeMatched,
    int EdgeMissing,
    int EdgeUnplanned,
    double ConformancePercent,
    IReadOnlyList<BaselineDriftEntryContract> Drifts);

/// <summary>
/// Resultado de uma consulta de reuso ao portfólio ANTES de criar uma nova entrega (ARC-10): sistemas
/// existentes que já cobrem a capacidade/domínio pedido (candidatos a reuso) e possíveis duplicidades.
/// </summary>
public sealed record PortfolioReuseCandidateContract(
    string SystemId, string Name, string? Domain, IReadOnlyList<string> MatchedCapabilities, bool DuplicateFlag);

public sealed record PortfolioReuseContract(
    string? Capability,
    string? Domain,
    int CandidateCount,
    IReadOnlyList<PortfolioReuseCandidateContract> Candidates);
