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
    /// Mínimo de INTELIGÊNCIAS distintas — contas diferentes — para o conselho valer como conselho.
    ///
    /// Assento é lente; conta é quem pensa. Seis personas na mesma conta são seis prompts para o
    /// mesmo modelo: a diversidade fica só na instrução, e a cegueira que ela deveria quebrar é
    /// exatamente a que se repete seis vezes com aparência de consenso. Foi o que aconteceu em
    /// 03/08/2026 — os seis pareceres da fase 4 saíram todos de `worker-antigravity-review`.
    ///
    /// Dois é o piso, não o ideal: é o menor número em que existe alguém que possa discordar por
    /// outro motivo, e não por outra pergunta.
    /// </summary>
    public const int MinimumDistinctAccounts = 2;

    /// <summary>
    /// Provedores distintos são PREFERIDOS, nunca exigidos. Duas contas do mesmo fornecedor já
    /// quebram a cegueira de sessão e de cota; exigir fornecedores diferentes transformaria uma
    /// preferência de qualidade em bloqueio de disponibilidade, e o conselho voltaria a ser
    /// impossível nas horas em que só um fornecedor responde.
    /// </summary>
    public const bool PreferDistinctProviders = true;

    /// <summary>
    /// Quantos assentos podem estar ABERTOS ao mesmo tempo, dada a capacidade real da frota.
    ///
    /// A regra que esta função existe para impor: capacidade curta faz o conselho SERIALIZAR, não
    /// bloquear. Abrir seis assentos com uma conta elegível não produz seis opiniões — produz seis
    /// cards disputando a mesma conta, cinco deles adiados a cada ciclo, e a fase parada com
    /// aparência de trabalho em andamento. Um assento de cada vez leva mais tempo de relógio e
    /// chega ao mesmo lugar, com a diferença de que se sabe onde ele está.
    ///
    /// O piso de 1 é deliberado: sem conta nenhuma elegível o conselho não é convocado, e quem
    /// decide isso é o escalonador — não este teto, que jamais deve devolver zero e travar a fase
    /// por aritmética.
    /// </summary>
    public static int MaximumConcurrentSeats(CouncilCapacity capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        return Math.Max(1, capacity.DistinctAccounts);
    }

    /// <summary>
    /// O que a mesa REALMENTE foi — não o que se pretendia que fosse.
    ///
    /// Opinião sem conta identificada conta como lente e não conta como inteligência: o número de
    /// contas distintas é sempre o que se pode PROVAR, nunca o que se pode supor. Um conselho que
    /// arredondasse para cima aqui estaria mentindo justamente no campo que existe para não deixar
    /// mentir.
    /// </summary>
    public static CouncilDiversity MeasureDiversity(IReadOnlyList<CouncilOpinion> opinions)
    {
        ArgumentNullException.ThrowIfNull(opinions);
        var heard = opinions.Where(opinion => !opinion.IsOperational).ToArray();
        var accounts = heard
            .Select(opinion => opinion.AccountAlias)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var providers = heard
            .Select(opinion => opinion.ProviderKind)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new CouncilDiversity(heard.Length, accounts, providers);
    }

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
    /// <remarks>
    /// A CHAVE É CONTRATO, não rótulo. Ela é resolvida contra o catálogo de personas; uma chave
    /// que não existe lá não falha — cai no fallback de persona inferida, e o assento executa com
    /// OUTRA lente. Medido em 03/08/2026: <c>playbook-po</c> não existia (o catálogo registra
    /// <c>playbook-product-owner</c>) e o assento de Produto foi executado pela persona Software
    /// Architect — justamente a lente que menos deveria se repetir numa mesa que já tem um
    /// arquiteto. O conselho continuou com seis assentos e uma lente a menos, sem nada acusar.
    ///
    /// Por isso <c>CouncilSeatCatalogContractTests</c> valida TODA chave contra o catálogo: a
    /// divergência precisa quebrar o build, não a semântica do conselho em silêncio.
    /// </remarks>
    public static IReadOnlyList<CouncilSeat> CoreSeats { get; } =
    [
        new("playbook-product-owner",
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
        // `playbook-devops`, e não `playbook-sre-sustentacao`: a lente pergunta pela SUBIDA e pelo
        // caminho de volta quando a entrega dá errado — release e rollback, que é o que o DevOps
        // do catálogo conduz. Sustentação responde pelo que já está de pé, e essa é outra pergunta.
        new(new CouncilSeat("playbook-devops",
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
        var diversity = MeasureDiversity(opinions);
        if (opinions.Count < MinimumCouncil)
        {
            return new CouncilVerdict(
                false,
                "council.incomplete",
                $"O conselho reuniu {opinions.Count} parecer(es); o mínimo é {MinimumCouncil}.",
                [],
                diversity);
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
                $"{blocking.Length} conselheiro(s) apontaram achado impeditivo antes do desenvolvimento. " +
                diversity.Declaration,
                dissent,
                diversity)
            : new CouncilVerdict(
                true,
                // A degradação NÃO reprova — ela é declarada. Bloquear o conselho por falta de
                // conta devolveria o impasse de 03/08 com outro nome; deixá-la passar calada
                // entregaria seis prompts do mesmo modelo como seis opiniões. O caminho honesto é
                // liberar dizendo em quantas inteligências aquilo foi pensado, e deixar o registro
                // decidir o quanto vale.
                diversity.ClearedReasonCode,
                "O conselho não encontrou impedimento para iniciar o desenvolvimento. " +
                diversity.Declaration,
                dissent,
                diversity);
    }

    /// <summary>
    /// Converte a saída REAL do card em parecer. Estado `done` sozinho não é opinião: sem resumo
    /// do executor não há evidência do que o conselheiro concluiu. O marcador explícito evita
    /// inferir consenso a partir de um card meramente fechado; o fallback preserva cards legados.
    /// </summary>
    public static CouncilOpinion? FromExecution(
        CouncilSeat seat,
        string? attemptSummary,
        string? blockedReason = null,
        string? accountAlias = null,
        string? providerKind = null)
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
                IsOperational: true,
                accountAlias,
                providerKind);
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
            summary.Length <= 2_000 ? summary : $"{summary[..2_000]}…",
            IsOperational: false,
            accountAlias,
            providerKind);
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
/// <param name="AccountAlias">
/// QUEM pensou este parecer — o alias da conta que executou, não a persona. É o campo que separa
/// "seis lentes" de "seis prompts para o mesmo modelo", e por isso ele é medido do ledger de
/// invocações da tentativa, nunca inferido do nome do assento. <see langword="null"/> significa
/// "não foi possível provar de qual conta veio", e conta como lente sem contar como inteligência.
/// </param>
/// <param name="ProviderKind">
/// O fornecedor por trás da conta. Serve à PREFERÊNCIA por provedores distintos, nunca a um
/// bloqueio: duas contas do mesmo fornecedor já quebram a cegueira de sessão e de cota.
/// </param>
public sealed record CouncilOpinion(
    string Seat,
    bool IsBlocking,
    bool HasConcern,
    string Summary,
    bool IsOperational = false,
    string? AccountAlias = null,
    string? ProviderKind = null);

/// <summary>
/// Capacidade real da frota no instante da convocação — quantas contas e quantos fornecedores
/// distintos podem, AGORA, ocupar um assento. Vem do escalonador, que é a única autoridade sobre
/// elegibilidade; o conselho não reimplementa essa decisão, ele a consome.
/// </summary>
public sealed record CouncilCapacity(int DistinctAccounts, int DistinctProviders);

/// <summary>
/// O que a mesa foi de verdade. Existe para que a frase "o conselho aprovou" nunca mais possa
/// esconder em quantas cabeças aquilo foi pensado.
/// </summary>
public sealed record CouncilDiversity(int Seats, int DistinctAccounts, int DistinctProviders)
{
    public bool MeetsMinimumDiversity =>
        DistinctAccounts >= AgentCouncilPolicy.MinimumDistinctAccounts;

    /// <summary>
    /// A diversidade não foi MEDIDA — o que é diferente de ter sido medida e ser insuficiente.
    /// Acontece quando nenhuma opinião pôde ser atribuída a uma conta (ledger indisponível, ou
    /// caminho que ainda não instrumenta autoria). Confundir os dois casos custaria os dois lados:
    /// tratar desconhecido como suficiente esconde monólogo; tratá-lo como degradado acusa uma
    /// frota curta que talvez não seja curta.
    /// </summary>
    public bool DiversityUnknown => DistinctAccounts == 0 && Seats > 0;

    /// <summary>
    /// O código de motivo de um conselho que LIBEROU. São três, e a diferença entre eles é a
    /// diferença entre saber, saber que é pouco, e não saber.
    /// </summary>
    public string ClearedReasonCode =>
        DiversityUnknown
            ? "council.cleared_diversity_unknown"
            : MeetsMinimumDiversity
                ? "council.cleared"
                : "council.cleared_degraded";

    /// <summary>
    /// A frase que vai para o ledger e para o portão. Ela é escrita para ser lida por quem não
    /// conhece o código: "N lentes, M inteligências distintas" diz, sem jargão, se aquele parecer
    /// foi um debate ou um monólogo com seis vozes.
    /// </summary>
    public string Declaration =>
        DistinctAccounts == 0
            ? $"{Seats} lente(s); não foi possível provar em quantas contas distintas foram " +
              "pensadas — a diversidade deste conselho é DESCONHECIDA."
            : MeetsMinimumDiversity
                ? $"{Seats} lente(s) em {DistinctAccounts} inteligência(s) distinta(s)" +
                  $"{(DistinctProviders > 1 ? $" de {DistinctProviders} fornecedores" : string.Empty)}."
                : $"DEGRADADO: {Seats} lente(s) em apenas {DistinctAccounts} inteligência(s) " +
                  $"distinta(s) — o mínimo é {AgentCouncilPolicy.MinimumDistinctAccounts}. As " +
                  "lentes diferem na pergunta, não em quem responde; leia este parecer como " +
                  "opinião única examinada por vários ângulos.";
}

/// <param name="Dissent">
/// Toda discordância, bloqueante ou não. Um conselho cuja divergência some do registro vira
/// carimbo — e carimbo não protege ninguém.
/// </param>
/// <param name="Diversity">
/// Em quantas inteligências distintas este veredito foi pensado. Nasce opcional porque o campo é
/// novo e há chamadores anteriores a ele; onde vier <see langword="null"/>, a resposta honesta é
/// "não medido" — jamais "diversidade suficiente".
/// </param>
public sealed record CouncilVerdict(
    bool MayProceed,
    string ReasonCode,
    string Rationale,
    IReadOnlyList<string> Dissent,
    CouncilDiversity? Diversity = null);
