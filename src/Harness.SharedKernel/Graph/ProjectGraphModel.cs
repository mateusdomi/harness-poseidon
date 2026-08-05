namespace Harness.SharedKernel.Graph;

/// <summary>
/// O modelo do grafo de projeto (Onda 1 — Graph-Driven Project Intelligence).
///
/// O grafo é uma PROJEÇÃO derivada das fontes canônicas (cards, demandas, fases, gates,
/// decisões, artefatos, evidência) — nunca fonte da verdade. Pode ser reconstruído do zero a
/// qualquer momento, e a reconstrução tem de produzir grafo IDÊNTICO ao incremental (teste de
/// equivalência). Tipos de nó e de aresta são conjuntos FECHADOS: uma relação que não couber
/// nos doze tipos abaixo não vira "relates_to" — vira discussão de modelagem, porque aresta
/// genérica é como um grafo morre: tudo conectado a tudo, nada explicando nada.
/// </summary>
public enum GraphNodeType
{
    Requirement,
    Nfr,
    HumanFact,
    Constraint,
    Decision,
    Risk,
    OpenQuestion,
    Artifact,
    Phase,
    Gate,
    Card,
    Test,
    Evidence,
}

/// <summary>Conjunto FECHADO de relações. Não existe aresta genérica — por invariante.</summary>
public enum GraphRelationType
{
    Implements,
    Verifies,
    Proves,
    DerivesFrom,
    ConstrainedBy,
    Impacts,
    DependsOn,
    BlockedBy,
    Supersedes,
    Invalidates,
    Requires,
    Produces,
}

public enum GraphProvenance
{
    /// <summary>Derivada de estrutura canônica conhecida. Confiança 1.0, status Accepted.</summary>
    Deterministic,

    /// <summary>Inferida por modelo. Nasce Proposed e NUNCA bloqueia readiness.</summary>
    ModelInference,
}

public enum GraphEdgeStatus
{
    /// <summary>Inferência aguardando promoção (regra determinística ou decisão humana).</summary>
    Proposed,

    /// <summary>Vale para travessia de impacto e para readiness.</summary>
    Accepted,
}

public enum GraphNodeState
{
    Active,

    /// <summary>
    /// Uma mudança upstream invalidou a premissa deste nó. STALE não apaga nem reverte nada:
    /// carrega a CAUSA (nó + versão que mudou) e gera trabalho de revalidação visível.
    /// </summary>
    Stale,

    /// <summary>A fonte canônica saiu de cena (card cancelado, decisão superseded).</summary>
    Retired,
}

/// <summary>
/// Um nó da projeção. <see cref="Id"/> é DETERMINÍSTICO (derivado de tipo + fonte canônica):
/// é o que torna a projeção idempotente e a reconstrução equivalente por construção.
/// </summary>
public sealed record GraphNode(
    string Id,
    string ProjectId,
    GraphNodeType Type,
    string CanonicalSourceId,
    string CanonicalSourceKind,
    int Version,
    GraphNodeState State,
    GraphProvenance Provenance,
    double Confidence,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? StaleCauseNodeId = null,
    int? StaleCauseVersion = null)
{
    public static string DeterministicId(GraphNodeType type, string canonicalSourceId) =>
        $"{type.ToString().ToLowerInvariant()}:{canonicalSourceId}";
}

/// <summary>
/// Uma aresta da projeção. Construa SEMPRE pelas fábricas <see cref="Structural"/> /
/// <see cref="Proposed"/> — são elas que impõem os invariantes da Onda 1 (estrutural =
/// Deterministic/1.0/Accepted; inferida = ModelInference/Proposed). O construtor primário
/// existe para desserialização de persistência, não para contornar as fábricas.
/// </summary>
public sealed record GraphEdge(
    string Id,
    string FromNodeId,
    string ToNodeId,
    GraphRelationType RelationType,
    GraphProvenance Provenance,
    double Confidence,
    GraphEdgeStatus Status,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil = null)
{
    public static string DeterministicId(
        GraphRelationType relation, string fromNodeId, string toNodeId) =>
        $"{relation.ToString().ToLowerInvariant()}:{fromNodeId}->{toNodeId}";

    /// <summary>Aresta ESTRUTURAL: fato canônico conhecido. Deterministic, 1.0, Accepted.</summary>
    public static GraphEdge Structural(
        string fromNodeId, string toNodeId, GraphRelationType relation, DateTimeOffset validFrom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toNodeId);
        return new GraphEdge(
            DeterministicId(relation, fromNodeId, toNodeId),
            fromNodeId, toNodeId, relation,
            GraphProvenance.Deterministic, 1.0, GraphEdgeStatus.Accepted, validFrom);
    }

    /// <summary>
    /// Aresta INFERIDA por modelo: nasce Proposed, com a confiança declarada, e nunca bloqueia
    /// readiness nem dispara invalidação — informa o digest até ser promovida.
    /// </summary>
    public static GraphEdge Proposed(
        string fromNodeId,
        string toNodeId,
        GraphRelationType relation,
        double confidence,
        DateTimeOffset validFrom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toNodeId);
        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        return new GraphEdge(
            DeterministicId(relation, fromNodeId, toNodeId),
            fromNodeId, toNodeId, relation,
            GraphProvenance.ModelInference, confidence, GraphEdgeStatus.Proposed, validFrom);
    }
}
