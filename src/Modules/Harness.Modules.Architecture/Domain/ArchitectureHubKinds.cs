namespace Harness.Modules.Architecture.Domain;

/// <summary>
/// Vocabulário CANÔNICO das áreas do Architecture Hub que estendem o modelo estruturado (ARC-01/05):
/// descoberta de sistemas existentes (ARC-06), racionalização (ARC-07), padrões &amp; decisões (ARC-08)
/// e a integração Delivery↔Architecture (ARC-10). Todos os enums são validados na borda para que dados
/// fora do vocabulário nunca entrem no modelo. Nada aqui é inventado: cada valor mapeia um fato gravado.
/// </summary>
public static class ArchitectureHubKinds
{
    // ARC-06 — Descoberta ----------------------------------------------------------------------------

    /// <summary>Fontes a partir das quais uma descoberta é registrada.</summary>
    public static readonly IReadOnlySet<string> DiscoverySource = new HashSet<string>(StringComparer.Ordinal)
    {
        "repo", "docs", "openapi", "schema", "dockerfile", "pipeline", "iac",
        "logs", "inventory", "interview", "spreadsheet", "diagram",
    };

    /// <summary>Confiança de uma informação descoberta — SEMPRE explícita, nunca presumida.</summary>
    public static readonly IReadOnlySet<string> Confidence = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high",
    };

    /// <summary>Ciclo de vida de uma descoberta: aberta até um humano confirmar ou rejeitar.</summary>
    public static readonly IReadOnlySet<string> DiscoveryStatus = new HashSet<string>(StringComparer.Ordinal)
    {
        "open", "confirmed", "rejected",
    };

    // ARC-07 — Racionalização ------------------------------------------------------------------------

    /// <summary>
    /// Classificação de racionalização. O agente SUGERE com impacto, NUNCA decide sozinho: cada valor é
    /// uma recomendação (proposta), não uma ação. Manter/Modernizar/Consolidar/Substituir/Desativar/Investigar.
    /// </summary>
    public static readonly IReadOnlySet<string> Classification = new HashSet<string>(StringComparer.Ordinal)
    {
        "keep", "modernize", "consolidate", "replace", "decommission", "investigate",
    };

    /// <summary>Categorias de sinal de racionalização derivadas estritamente de fatos gravados.</summary>
    public static readonly IReadOnlySet<string> InsightCategory = new HashSet<string>(StringComparer.Ordinal)
    {
        "functional-overlap", "unification", "decommission", "unsupported-tech",
        "point-to-point", "direct-db-access", "spof", "cost-vs-use",
    };

    // ARC-08 — Padrões & Decisões --------------------------------------------------------------------

    /// <summary>Tipo de item do acervo: um ADR corporativo ou um padrão reutilizável.</summary>
    public static readonly IReadOnlySet<string> PatternKind = new HashSet<string>(StringComparer.Ordinal)
    {
        "adr", "pattern",
    };

    /// <summary>Status de um ADR (decisão) — o ciclo canônico de decisões arquiteturais.</summary>
    public static readonly IReadOnlySet<string> AdrStatus = new HashSet<string>(StringComparer.Ordinal)
    {
        "proposed", "accepted", "rejected", "deprecated", "superseded",
    };

    /// <summary>Status de um padrão reutilizável.</summary>
    public static readonly IReadOnlySet<string> PatternStatus = new HashSet<string>(StringComparer.Ordinal)
    {
        "recommended", "experimental", "deprecated",
    };

    // ARC-10 — Baseline da entrega -------------------------------------------------------------------

    /// <summary>
    /// Estado de uma baseline de entrega: 'baseline' (arquitetura aprovada), 'as_built' (produção
    /// atualizou o AS-IS) e 'closed' (encerramento comparou proposta × implementação).
    /// </summary>
    public static readonly IReadOnlySet<string> BaselineStatus = new HashSet<string>(StringComparer.Ordinal)
    {
        "baseline", "as_built", "closed",
    };

    public const string Adr = "adr";
    public const string Pattern = "pattern";
    public const string StatusOpen = "open";
    public const string StatusBaseline = "baseline";
    public const string StatusAsBuilt = "as_built";
    public const string StatusClosed = "closed";

    public static bool IsDiscoverySource(string? value) => value is not null && DiscoverySource.Contains(value);
    public static bool IsConfidence(string? value) => value is not null && Confidence.Contains(value);
    public static bool IsDiscoveryStatus(string? value) => value is not null && DiscoveryStatus.Contains(value);
    public static bool IsPatternKind(string? value) => value is not null && PatternKind.Contains(value);

    /// <summary>Rank numérico da confiança (low=1..high=3) para agregações determinísticas.</summary>
    public static int ConfidenceRank(string? value) => value switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };
}
