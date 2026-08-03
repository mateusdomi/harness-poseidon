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
    /// <summary>
    /// NÚCLEO OBRIGATÓRIO: as lentes que todo planejamento precisa, qualquer que seja o projeto.
    ///
    /// Produto responde se é a coisa certa, arquitetura se a decisão se sustenta, tech lead se
    /// dá para executar. Nenhuma delas depende do domínio — um planejamento sem essas três
    /// respostas não foi criticado, foi carimbado.
    /// </summary>
    public static IReadOnlyList<CouncilSeat> CoreSeats { get; } =
    [
        new("playbook-po",
            "Isto resolve o problema que a pessoa trouxe? O escopo entrega valor verificável ou " +
            "só entrega atividade?"),
        new("playbook-arquiteto",
            "As decisões de arquitetura se sustentam? Cada trade-off declarou o que custa, e a " +
            "visão restrita cabe nas restrições reais do projeto?"),
        new("playbook-tech-lead",
            "Este planejamento é executável? Os cards têm critério verificável, tamanho seguro e " +
            "dependências que permitem paralelismo de verdade?"),
    ];

    /// <summary>
    /// Assentos de RISCO E DOMÍNIO: entram quando o projeto os justifica, não por ritual.
    ///
    /// Convocar cinco especialistas para todo planejamento tem dois custos, e o segundo é pior
    /// que o primeiro: gasta cota, e ensina a fábrica a tratar o conselho como formalidade. Um
    /// parecer de segurança sobre um projeto sem superfície externa é ruído — e ruído repetido
    /// é como uma sala inteira aprende a assinar sem ler.
    /// </summary>
    public static IReadOnlyList<ConditionalSeat> ConditionalSeats { get; } =
    [
        new(new CouncilSeat("playbook-security",
                "O que um atacante faria com isto? O threat model cobre os ativos que o " +
                "planejamento introduziu, e o risco residual está nomeado com quem o aceita?"),
            context => context.ExternalSurface || context.Authentication ||
                context.SensitiveData || context.SecurityRisk),
        new(new CouncilSeat("playbook-dba-dados",
                "O modelo sustenta as consultas e o crescimento reais, ou só o diagrama?"),
            context => context.Persistence || context.Migration ||
                context.HighVolume || context.Analytics),
        new(new CouncilSeat("playbook-sre-devops",
                "Isto sobe, fica de pé e avisa quando cai? Existe caminho de volta quando a " +
                "entrega der errado em produção?"),
            context => context.Deployment || context.Infrastructure ||
                context.Observability || context.Availability),
        new(new CouncilSeat("playbook-qa",
                "Dá para PROVAR que ficou pronto? Todo critério de aceite é verificável por " +
                "terceiro, e a estratégia de teste existe antes do código?"),
            context => context.TestableCriteria || context.QualityStrategy),
    ];

    /// <summary>
    /// Todos os assentos que a política conhece. Serve à leitura e à compatibilidade de quem
    /// enumerava o conselho inteiro; a CONVOCAÇÃO usa <see cref="SelectSeats"/>.
    /// </summary>
    public static IReadOnlyList<CouncilSeat> Seats { get; } =
        [.. CoreSeats, .. ConditionalSeats.Select(seat => seat.Seat)];

    /// <summary>
    /// Quem senta nesta mesa.
    ///
    /// Núcleo obrigatório, mais os assentos que o contexto justifica, mais quem a Bruna convocar
    /// com justificativa. O piso de <see cref="MinimumCouncil"/> continua valendo: um projeto que
    /// não justifica nenhum assento condicional ainda assim tem três lentes distintas, porque uma
    /// divergência isolada precisa aparecer como divergência e não como maioria.
    ///
    /// A ordem é estável — núcleo, condicionais na ordem declarada, extras da Bruna — para que o
    /// mesmo contexto produza sempre a mesma mesa, e o conselho seja reproduzível.
    /// </summary>
    public static IReadOnlyList<CouncilSeat> SelectSeats(
        CouncilContext context,
        IReadOnlyList<CouncilSeat>? brunaOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var selected = new List<CouncilSeat>(CoreSeats);
        selected.AddRange(ConditionalSeats
            .Where(candidate => candidate.Applies(context))
            .Select(candidate => candidate.Seat));

        // A Bruna pode convocar competência que a tabela não previu — é dela a decisão de quem
        // precisa opinar. O que ela não pode é REMOVER o núcleo: override amplia, nunca reduz.
        if (brunaOverride is { Count: > 0 })
        {
            selected.AddRange(brunaOverride.Where(extra =>
                !selected.Any(seat => string.Equals(
                    seat.PersonaKey, extra.PersonaKey, StringComparison.Ordinal))));
        }

        return selected;
    }

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
        // Bloqueio SEM parecer é ausência de opinião, não opinião contrária. O que se sabe é que
        // este assento não pôde ser ouvido — e é isso que precisa ser dito.
        if (!string.IsNullOrWhiteSpace(blockedReason) && string.IsNullOrWhiteSpace(attemptSummary))
        {
            return new CouncilOpinion(
                seat.PersonaKey,
                IsBlocking: true,
                HasConcern: false,
                $"Este conselheiro não pôde ser ouvido: {blockedReason.Trim()}",
                IsOperational: true);
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

/// <summary>Um assento que só senta quando o projeto o justifica.</summary>
public sealed record ConditionalSeat(CouncilSeat Seat, Func<CouncilContext, bool> Applies);

/// <summary>
/// O que este projeto tem, para decidir quem precisa opinar.
///
/// Todas as marcas nascem `false`: um projeto só convoca o especialista de segurança se
/// alguém AFIRMOU que há superfície externa, autenticação, dado sensível ou risco. O padrão
/// silencioso é a mesa mínima — e a mesa mínima ainda tem três lentes distintas.
/// </summary>
public sealed record CouncilContext(
    bool ExternalSurface = false,
    bool Authentication = false,
    bool SensitiveData = false,
    bool SecurityRisk = false,
    bool Persistence = false,
    bool Migration = false,
    bool HighVolume = false,
    bool Analytics = false,
    bool Deployment = false,
    bool Infrastructure = false,
    bool Observability = false,
    bool Availability = false,
    bool TestableCriteria = false,
    bool QualityStrategy = false);

/// <param name="HasConcern">
/// Ressalva que não bloqueia. Fica no ledger mesmo assim: a ressalva de hoje costuma ser o
/// incidente de depois, e apagá-la por não bloquear é perder o aviso.
/// </param>
/// <param name="IsOperational">
/// O assento NÃO opinou: o card do parecer foi bloqueado por uma causa operacional (não pôde ser
/// despachado, o executor falhou, escalou). Continua segurando a fase — um conselho incompleto não
/// libera nada —, mas não é achado técnico e não gera card de correção. Sem essa distinção, uma
/// falha de infraestrutura era publicada como parecer do arquiteto, e o Control Plane abria
/// trabalho para "corrigir" uma opinião que ninguém deu.
/// </param>
public sealed record CouncilOpinion(
    string Seat,
    bool IsBlocking,
    bool HasConcern,
    string Summary,
    bool IsOperational = false);

/// <param name="Dissent">
/// Toda discordância, bloqueante ou não. Um conselho cuja divergência some do registro vira
/// carimbo — e carimbo não protege ninguém.
/// </param>
public sealed record CouncilVerdict(
    bool MayProceed,
    string ReasonCode,
    string Rationale,
    IReadOnlyList<string> Dissent);
