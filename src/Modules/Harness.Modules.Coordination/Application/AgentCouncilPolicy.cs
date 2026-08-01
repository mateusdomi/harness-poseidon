namespace Harness.Modules.Coordination.Application;

/// <summary>
/// B13 — o CONSELHO DE AGENTES: quando várias especialidades revisam, em contexto fresco, o
/// conjunto de decisões acumuladas antes de a fábrica começar a construir.
///
/// O gatilho é de POLÍTICA, não de julgamento da chefe: um conselho que dependesse de ela lembrar
/// de convocá-lo aconteceria nas fases em que menos importa e faltaria justamente onde o erro é
/// caro. O ponto que importa é a passagem do Planejamento para o Desenvolvimento — dali em diante,
/// cada decisão errada custa código escrito, revisado e refeito. Quanto melhor o planejamento,
/// mais barato o desenvolvimento.
///
/// O conselho NÃO decide: ele critica. A decisão continua sendo da chefe, e os dissensos ficam
/// registrados — um conselho cuja discordância some do ledger vira carimbo, e carimbo não protege
/// ninguém.
/// </summary>
public static class AgentCouncilPolicy
{
    public const int MaximumReviewCycles = 3;
    /// <summary>
    /// A fase cuja SAÍDA aciona o conselho. É a última em que corrigir ainda é barato: depois
    /// dela o custo do erro passa a ser medido em código refeito.
    /// </summary>
    public const string TriggerPhase = "4-Planejamento";

    /// <summary>
    /// Mínimo de conselheiros para o parecer valer. Dois produzem empate sem desempate e três é o
    /// menor número em que uma divergência isolada aparece como divergência, e não como maioria.
    /// </summary>
    public const int MinimumCouncil = 3;

    /// <summary>
    /// As lentes do conselho, cada uma com a pergunta que só ela faz.
    ///
    /// A diversidade é o ponto: três agentes com a mesma lente produzem a mesma cegueira três
    /// vezes e dão a ela aparência de consenso.
    /// </summary>
    public static IReadOnlyList<CouncilSeat> Seats { get; } =
    [
        new("playbook-arquiteto",
            "As decisões de arquitetura se sustentam? Cada trade-off declarou o que custa, e a " +
            "visão restrita cabe nas restrições reais do projeto?"),
        new("playbook-tech-lead",
            "Este planejamento é executável? Os cards têm critério verificável, tamanho seguro e " +
            "dependências que permitem paralelismo de verdade?"),
        new("playbook-qa",
            "Dá para PROVAR que ficou pronto? Todo critério de aceite é verificável por terceiro, " +
            "e a estratégia de teste existe antes do código?"),
        new("playbook-security",
            "O que um atacante faria com isto? O threat model cobre os ativos que o planejamento " +
            "introduziu, e o risco residual está nomeado com quem o aceita?"),
        new("playbook-dba-dados",
            "O modelo sustenta as consultas e o crescimento reais, ou só o diagrama?"),
    ];

    /// <summary>
    /// O conselho deve ser convocado nesta transição?
    ///
    /// Só na saída do Planejamento, e só quando há material para criticar: convocar cinco
    /// especialistas para revisar um planejamento vazio gasta cota e ensina a fábrica a tratar o
    /// conselho como ritual.
    /// </summary>
    public static bool ShouldConvene(string phaseName, int producedDocumentCount) =>
        string.Equals(phaseName, TriggerPhase, StringComparison.OrdinalIgnoreCase) &&
        producedDocumentCount > 0;

    /// <summary>
    /// Consolida os pareceres.
    ///
    /// Um único veredito BLOQUEANTE segura a transição — o conselho não é votação por maioria.
    /// Se o especialista de segurança encontra uma falha explorável, o fato de outros quatro
    /// acharem o planejamento bom não torna a falha menos explorável. Maioria decide preferência;
    /// evidência decide risco.
    /// </summary>
    public static CouncilVerdict Consolidate(IReadOnlyList<CouncilOpinion> opinions)
    {
        ArgumentNullException.ThrowIfNull(opinions);
        if (opinions.Count < MinimumCouncil)
        {
            return new CouncilVerdict(
                false,
                "council.incomplete",
                $"O conselho reuniu {opinions.Count} parecer(es); o mínimo é {MinimumCouncil}.",
                []);
        }

        var blocking = opinions.Where(opinion => opinion.IsBlocking).ToArray();
        var dissent = opinions
            .Where(opinion => opinion.IsBlocking || opinion.HasConcern)
            .Select(opinion => $"{opinion.Seat}: {opinion.Summary}")
            .ToArray();

        return blocking.Length > 0
            ? new CouncilVerdict(
                false,
                "council.blocking_finding",
                $"{blocking.Length} conselheiro(s) apontaram achado impeditivo antes do desenvolvimento.",
                dissent)
            : new CouncilVerdict(
                true,
                "council.cleared",
                "O conselho não encontrou impedimento para iniciar o desenvolvimento.",
                dissent);
    }

    /// <summary>
    /// Converte a saída REAL do card em parecer. Estado `done` sozinho não é opinião: sem resumo
    /// do executor não há evidência do que o conselheiro concluiu. O marcador explícito evita
    /// inferir consenso a partir de um card meramente fechado; o fallback preserva cards legados.
    /// </summary>
    public static CouncilOpinion? FromExecution(
        CouncilSeat seat,
        string? attemptSummary,
        string? blockedReason = null)
    {
        ArgumentNullException.ThrowIfNull(seat);
        if (!string.IsNullOrWhiteSpace(blockedReason))
        {
            return new CouncilOpinion(
                seat.PersonaKey, true, false, blockedReason.Trim());
        }

        if (string.IsNullOrWhiteSpace(attemptSummary))
        {
            return null;
        }

        var summary = attemptSummary.Trim();
        var verdict = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("VEREDITO:", StringComparison.OrdinalIgnoreCase));
        var blocking = verdict?.Contains("BLOQUEAR", StringComparison.OrdinalIgnoreCase) == true;
        var concern = verdict?.Contains("RESSALVA", StringComparison.OrdinalIgnoreCase) == true;
        if (verdict is null)
        {
            // Compatibilidade com pareceres produzidos antes do contrato estruturado. A ausência
            // do marcador nunca vira concordância silenciosa: no mínimo é uma ressalva auditável.
            blocking = summary.Contains("ACHADO IMPEDITIVO", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("BLOCKING", StringComparison.OrdinalIgnoreCase);
            concern = !blocking;
        }

        return new CouncilOpinion(
            seat.PersonaKey, blocking, concern,
            summary.Length <= 2_000 ? summary : $"{summary[..2_000]}…");
    }
}

/// <param name="Lens">A pergunta que este assento faz — e que nenhum outro faz igual.</param>
public sealed record CouncilSeat(string PersonaKey, string Lens);

/// <param name="HasConcern">
/// Ressalva que não bloqueia. Fica no ledger mesmo assim: a ressalva de hoje costuma ser o
/// incidente de depois, e apagá-la por não bloquear é perder o aviso.
/// </param>
public sealed record CouncilOpinion(
    string Seat,
    bool IsBlocking,
    bool HasConcern,
    string Summary);

/// <param name="Dissent">
/// Toda discordância, bloqueante ou não. Um conselho cuja divergência some do registro vira
/// carimbo — e carimbo não protege ninguém.
/// </param>
public sealed record CouncilVerdict(
    bool MayProceed,
    string ReasonCode,
    string Rationale,
    IReadOnlyList<string> Dissent);
