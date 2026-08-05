using Harness.SharedKernel.Graph;

namespace Harness.Persistence.Abstractions.Graph;

/// <summary>
/// A store da ProjectGraphProjection (Onda 1).
///
/// <see cref="ApplyAsync"/> recebe o grafo DESEJADO e materializa a diferença: upsert do que
/// mudou, remoção do que sumiu, <c>created_at</c> preservado para ids que já existiam, e uma
/// linha de snapshot com a versão monotônica nova. É a mesma porta para o incremental e para o
/// `graph rebuild` — a equivalência dos dois caminhos é invariante de arquitetura, não acaso.
///
/// STALE é estado OPERACIONAL, não derivado das fontes: um apply que não mexeu na versão do nó
/// preserva o STALE existente (e a causa); um apply que avançou a versão o limpa — a mudança da
/// própria fonte é exatamente o que a revalidação esperava.
/// </summary>
public interface IProjectGraphStore
{
    Task<ProjectGraphStoreSnapshot> GetAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default);

    Task<long> ApplyAsync(
        ProjectGraphApplyCommand command, CancellationToken cancellationToken = default);

    /// <summary>Marca nós como Stale com a CAUSA (nó + versão). Nunca apaga nem reverte nada.</summary>
    Task MarkStaleAsync(
        ProjectGraphStaleCommand command, CancellationToken cancellationToken = default);

    /// <summary>Revalidação aprovada: limpa o STALE do nó, voltando-o a Active.</summary>
    Task ClearStaleAsync(
        string tenantId, string projectId, string nodeId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectGraphStoreSnapshot(
    long Version,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

public sealed record ProjectGraphApplyCommand(
    string TenantId,
    string ProjectId,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    string Reason,
    DateTimeOffset OccurredAt);

public sealed record ProjectGraphStaleCommand(
    string TenantId,
    string ProjectId,
    IReadOnlyList<GraphStaleMark> Marks,
    DateTimeOffset OccurredAt);

public sealed record GraphStaleMark(string NodeId, string CauseNodeId, int CauseVersion);

/// <summary>Mapeamento fechado enum ↔ texto do banco (snake_case), compartilhado pelas stores.</summary>
public static class GraphStorageNames
{
    public static string Of(GraphNodeType type) => type switch
    {
        GraphNodeType.Requirement => "requirement",
        GraphNodeType.Nfr => "nfr",
        GraphNodeType.HumanFact => "human_fact",
        GraphNodeType.Constraint => "constraint",
        GraphNodeType.Decision => "decision",
        GraphNodeType.Risk => "risk",
        GraphNodeType.OpenQuestion => "open_question",
        GraphNodeType.Artifact => "artifact",
        GraphNodeType.Phase => "phase",
        GraphNodeType.Gate => "gate",
        GraphNodeType.Card => "card",
        GraphNodeType.Test => "test",
        GraphNodeType.Evidence => "evidence",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static GraphNodeType NodeType(string value) => value switch
    {
        "requirement" => GraphNodeType.Requirement,
        "nfr" => GraphNodeType.Nfr,
        "human_fact" => GraphNodeType.HumanFact,
        "constraint" => GraphNodeType.Constraint,
        "decision" => GraphNodeType.Decision,
        "risk" => GraphNodeType.Risk,
        "open_question" => GraphNodeType.OpenQuestion,
        "artifact" => GraphNodeType.Artifact,
        "phase" => GraphNodeType.Phase,
        "gate" => GraphNodeType.Gate,
        "card" => GraphNodeType.Card,
        "test" => GraphNodeType.Test,
        "evidence" => GraphNodeType.Evidence,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(GraphRelationType relation) => relation switch
    {
        GraphRelationType.Implements => "implements",
        GraphRelationType.Verifies => "verifies",
        GraphRelationType.Proves => "proves",
        GraphRelationType.DerivesFrom => "derives_from",
        GraphRelationType.ConstrainedBy => "constrained_by",
        GraphRelationType.Impacts => "impacts",
        GraphRelationType.DependsOn => "depends_on",
        GraphRelationType.BlockedBy => "blocked_by",
        GraphRelationType.Supersedes => "supersedes",
        GraphRelationType.Invalidates => "invalidates",
        GraphRelationType.Requires => "requires",
        GraphRelationType.Produces => "produces",
        _ => throw new ArgumentOutOfRangeException(nameof(relation)),
    };

    public static GraphRelationType RelationType(string value) => value switch
    {
        "implements" => GraphRelationType.Implements,
        "verifies" => GraphRelationType.Verifies,
        "proves" => GraphRelationType.Proves,
        "derives_from" => GraphRelationType.DerivesFrom,
        "constrained_by" => GraphRelationType.ConstrainedBy,
        "impacts" => GraphRelationType.Impacts,
        "depends_on" => GraphRelationType.DependsOn,
        "blocked_by" => GraphRelationType.BlockedBy,
        "supersedes" => GraphRelationType.Supersedes,
        "invalidates" => GraphRelationType.Invalidates,
        "requires" => GraphRelationType.Requires,
        "produces" => GraphRelationType.Produces,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(GraphNodeState state) => state switch
    {
        GraphNodeState.Active => "active",
        GraphNodeState.Stale => "stale",
        GraphNodeState.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static GraphNodeState NodeState(string value) => value switch
    {
        "active" => GraphNodeState.Active,
        "stale" => GraphNodeState.Stale,
        "retired" => GraphNodeState.Retired,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(GraphProvenance provenance) => provenance switch
    {
        GraphProvenance.Deterministic => "deterministic",
        GraphProvenance.ModelInference => "model_inference",
        _ => throw new ArgumentOutOfRangeException(nameof(provenance)),
    };

    public static GraphProvenance Provenance(string value) => value switch
    {
        "deterministic" => GraphProvenance.Deterministic,
        "model_inference" => GraphProvenance.ModelInference,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string Of(GraphEdgeStatus status) => status switch
    {
        GraphEdgeStatus.Proposed => "proposed",
        GraphEdgeStatus.Accepted => "accepted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static GraphEdgeStatus EdgeStatus(string value) => value switch
    {
        "proposed" => GraphEdgeStatus.Proposed,
        "accepted" => GraphEdgeStatus.Accepted,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
