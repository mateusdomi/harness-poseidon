namespace Harness.Modules.Architecture.Domain;

/// <summary>
/// Vocabulário CANÔNICO do modelo arquitetural estruturado (ARC-01). O Hub mantém um MODELO
/// (elementos + relacionamentos), nunca imagens; diagramas são VIEWS (seleções) sobre esse modelo.
/// Todos os enums são validados na borda para que dados fora do vocabulário nunca entrem no modelo.
/// </summary>
public static class ArchitectureKinds
{
    /// <summary>Tipos de elemento (nível C4/ArchiMate simplificado).</summary>
    public static readonly IReadOnlySet<string> Element = new HashSet<string>(StringComparer.Ordinal)
    {
        "system", "container", "component", "dataStore", "actor", "external",
    };

    /// <summary>Tipos de relacionamento entre elementos.</summary>
    public static readonly IReadOnlySet<string> Relationship = new HashSet<string>(StringComparer.Ordinal)
    {
        "uses", "depends-on", "calls", "contains", "flows-to",
    };

    /// <summary>
    /// Estados do modelo mantidos SEPARADOS (ARC-05): o 'implemented' é a arquitetura vigente; o
    /// 'proposed' é a arquitetura proposta por um agente, que só passa a vigente após APLICAR.
    /// </summary>
    public static readonly IReadOnlySet<string> State = new HashSet<string>(StringComparer.Ordinal)
    {
        "implemented", "proposed",
    };

    /// <summary>Tipo de mudança que um item proposto representa sobre o modelo vigente.</summary>
    public static readonly IReadOnlySet<string> ChangeKind = new HashSet<string>(StringComparer.Ordinal)
    {
        "add", "modify", "remove",
    };

    /// <summary>Arestas que expressam DEPENDÊNCIA para a consulta "quem depende de X".</summary>
    public static readonly IReadOnlySet<string> DependencyRelationship = new HashSet<string>(StringComparer.Ordinal)
    {
        "uses", "depends-on", "calls", "flows-to",
    };

    public const string Implemented = "implemented";
    public const string Proposed = "proposed";
    public const string SystemKind = "system";

    public static bool IsElementKind(string? kind) => kind is not null && Element.Contains(kind);
    public static bool IsRelationshipKind(string? kind) => kind is not null && Relationship.Contains(kind);
    public static bool IsState(string? state) => state is not null && State.Contains(state);
    public static bool IsChangeKind(string? kind) => kind is not null && ChangeKind.Contains(kind);
    public static bool IsDependencyEdge(string? kind) => kind is not null && DependencyRelationship.Contains(kind);
}
