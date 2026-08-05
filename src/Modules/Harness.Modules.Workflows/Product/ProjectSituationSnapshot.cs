namespace Harness.Modules.Workflows.Product;

/// <summary>
/// GRAPH PHASE 0 — a fotografia da situação de um projeto, composta das derivações que já existem.
///
/// O que isto É: um read model. DERIVADO. Cada campo vem de um mecanismo que já tem dono e teste —
/// cobertura de requisito, correções do portão, guarda de não-progresso, estado de atenção. Este
/// tipo não calcula nada novo: ele JUNTA, porque a pergunta do portfólio ("onde está cada projeto,
/// o que está travado, quem precisa de humano?") atravessa mecanismos que ninguém via lado a lado.
///
/// O que isto NÃO é, por decisão registrada: não é fonte de verdade, não é Neo4j, não muda
/// scheduler, não muda gate, não entra no contexto da Bruna por default. A perícia do run real
/// classificou dois incidentes como GRAPH_WOULD_HELP (cards órfãos e deadlock de dependência) e
/// dois como GRAPH_NOT_RELEVANT — a fatia útil é exatamente a que está aqui: fronteira executável,
/// itens bloqueantes e requisitos descobertos. CriticalPath fica de fora porque os dados reais
/// (duração por card, dependências explícitas) ainda não sustentam um caminho crítico honesto.
/// </summary>
public sealed record ProjectSituationSnapshot(
    string ProjectId,
    string ProjectName,
    ProjectAttentionState Attention,

    /// <summary>Cards prontos e despacháveis AGORA — a fronteira executável.</summary>
    IReadOnlyList<string> RunnableFrontier,

    /// <summary>O que segura o resto: cards bloqueados/escalados, com o requisito que dependem deles.</summary>
    IReadOnlyList<string> BlockingItems,

    /// <summary>Requisitos sem dono vivo — a lacuna que custou a interface no run real.</summary>
    IReadOnlyList<RequirementCoverage> UncoveredRequirements,

    /// <summary>Lacunas do portão que já viraram trabalho corretivo.</summary>
    IReadOnlyList<string> CorrectiveWork,

    /// <summary>As paredes em que o projeto está batendo repetidamente.</summary>
    IReadOnlyList<string> RepeatedFailures,

    /// <summary>Premissas de NFR em vigor — o que foi assumido e precisa ser visível.</summary>
    IReadOnlyList<string> Assumptions)
{
    /// <summary>Nada executável e nada coberto pendente: ou terminou, ou está esperando gente.</summary>
    public bool Idle => RunnableFrontier.Count == 0 && CorrectiveWork.Count == 0;
}

/// <summary>
/// Compõe a fotografia a partir das derivações existentes. Puro: quem carrega os dados é o
/// chamador, e é assim que o read model continua sendo leitura — sem consulta própria, não há como
/// ele virar uma segunda verdade por acidente.
/// </summary>
public static class ProjectSituationComposer
{
    public static ProjectSituationSnapshot Compose(
        string projectId,
        string projectName,
        ProjectAttentionFacts attention,
        IReadOnlyList<RequirementCoverage> coverage,
        IReadOnlyList<(string CardId, bool Dispatchable, bool Blocked)> cards,
        IReadOnlyList<ProductGapCorrection> corrections,
        NoProgressVerdict noProgress,
        OperationalProfile? operationalProfile = null)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(corrections);
        ArgumentNullException.ThrowIfNull(noProgress);

        return new ProjectSituationSnapshot(
            projectId,
            projectName,
            ProjectAttentionClassifier.Classify(attention),
            [.. cards.Where(card => card.Dispatchable).Select(card => card.CardId)],
            [.. cards.Where(card => card.Blocked).Select(card => card.CardId)],
            [.. RequirementCoverageAnalyzer.Blocking(coverage)],
            [.. corrections.Select(correction => correction.Title)],
            noProgress.BlindRetryForbidden ? [.. noProgress.DominantGaps] : [],
            operationalProfile?.Assumptions ?? []);
    }
}
