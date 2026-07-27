using System.Globalization;
using System.Text.RegularExpressions;

namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Fatos PUROS de uma demanda já coletados do board, mais dicas explícitas de superfície. Nenhum
/// IO: o planner é uma função determinística. <see cref="Title"/> normalmente carrega o id estável
/// da feature (ex.: "CAT-04"); <see cref="AcceptanceCriteria"/> é a lista de critérios de aceite;
/// <see cref="RiskTier"/> é a prioridade/risco herdada da demanda.
/// </summary>
public sealed record DemandDecompositionRequest(
    string Title,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    string RiskTier,
    DemandDecompositionHints? Hints = null);

/// <summary>
/// Dicas explícitas que SOBRESCREVEM a heurística por palavra-chave quando presentes (não nulas).
/// Deixe nulo para deixar o planner inferir a superfície a partir do texto da demanda.
/// </summary>
public sealed record DemandDecompositionHints(
    bool? HasFrontendSurface = null,
    bool? RequiresExternalCredential = null,
    bool? HasTechnicalUncertainty = null,
    bool? RequiresDecision = null,

    /// <summary>
    /// A demanda produz CÓDIGO de servidor? Uma demanda cujo entregável é uma decisão (ADR), uma
    /// investigação ou um documento não produz — e emitir uma fatia de backend para ela manda um
    /// agente escrever código de produção para algo que ainda nem foi decidido.
    /// </summary>
    bool? HasImplementationSurface = null);

/// <summary>
/// Um card proposto do plano. <see cref="ProposedTitle"/> começa sempre pelo código estável
/// "&lt;featureId&gt;/T&lt;nn&gt;" (ex.: "CAT-04/T01"); <see cref="Dependencies"/> referencia esses
/// mesmos códigos. <see cref="CardType"/> é o tipo canônico do card ('feature','agent_task',
/// 'human_gate','spike','decision'); <see cref="RequiredRole"/> é o PAPEL necessário
/// (backend-specialist/frontend-specialist/critic/none), nunca uma conta específica.
/// </summary>
public sealed record ProposedCard(
    string ProposedTitle,
    string CardType,
    string RequiredRole,
    string Instruction,
    string InScope,
    string OutOfScope,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Gates,
    IReadOnlyList<string> Dependencies);

/// <summary>Plano proposto: o id da feature e a lista ORDENADA de cards filhos.</summary>
public sealed record DemandPlanProposal(string FeatureId, IReadOnlyList<ProposedCard> Cards);

/// <summary>
/// Planner PURO e determinístico que decompõe uma demanda canônica em cards filhos ATÔMICOS,
/// seguindo a política de criação de cards do dono do produto. Espelha a forma dos avaliadores puros
/// (<c>CardReadinessEvaluator</c>, <c>ReadinessEvaluator</c>): sem IO, sem relógio, sem aleatoriedade
/// — a mesma entrada produz sempre a mesma saída.
///
/// Regras (nesta ordem de emissão, códigos T01..Tnn):
/// 1. incerteza técnica (palavras-chave/dica) → um card 'spike' (NUNCA auto-despachável);
/// 2. credencial externa/homologação (palavras-chave/dica) → um card 'human_gate' (exige humano);
/// 3. decisão pendente (palavras-chave/dica) → um card 'decision' (exige humano);
/// 4. SEMPRE uma fatia de backend ('agent_task', backend-specialist);
/// 5. superfície de frontend (palavras-chave/dica) → uma fatia de frontend ('agent_task',
///    frontend-specialist);
/// 6. superfície de documentação (palavras-chave) → um card de documentação ('agent_task', none);
/// 7. SEMPRE um card final de integração ('human_gate', none) que DEPENDE dos cards de
///    implementação (backend/frontend/documentação): a revisão independente já ocorre em cada card
///    de implementação (ator≠crítico) e a integração é o merge, que é gate humano por regra.
/// Os cards de implementação dependem do spike e do human_gate quando estes existem — a incerteza é
/// resolvida e a credencial é provisionada antes de construir.
/// </summary>
public static class DemandDecompositionPlanner
{
    public const string RoleBackend = "backend-specialist";
    public const string RoleFrontend = "frontend-specialist";
    public const string RoleCritic = "critic";
    public const string RoleNone = "none";

    public const string CardTypeAgentTask = "agent_task";
    public const string CardTypeHumanGate = "human_gate";
    public const string CardTypeSpike = "spike";
    public const string CardTypeDecision = "decision";

    public const string FallbackFeatureId = "FEAT";

    private static readonly Regex FeatureIdPattern =
        new(@"[A-Za-z]{2,}-\d{1,4}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] FrontendTerms =
    [
        "frontend", "front-end", "front end", " ui ", "ui/", "/ui", " ux ", "tela", "telas",
        "screen", "component", "componente", "css", "react", "página", "pagina", "page ",
        "dashboard", "botão", "botao", "formulário", "formulario", "layout", "widget",
    ];

    private static readonly string[] ExternalCredentialTerms =
    [
        "credencial", "credential", "homologa", "homologação", "homologacao", "oauth", "api key",
        "apikey", "chave de api", "gateway de pagamento", "payment gateway", "certificado digital",
        "conta externa", "acesso externo", "provedor externo", "sandbox de terceiro", "gov.br",
        "produção externa", "producao externa", "cartório", "cartorio",
    ];

    private static readonly string[] UncertaintyTerms =
    [
        "incerteza", "incerto", "uncertain", "investigar", "investigate", "spike",
        "prova de conceito", "proof of concept", "poc ", " poc", "viabilidade", "feasibility",
        "pesquisar", "research", "explorar", "explore", "desconhecido", "unknown", "a definir",
        "tbd", "não está claro", "nao esta claro", "avaliar abordagem",
    ];

    private static readonly string[] DocumentationTerms =
    [
        "document", "documenta", "documentação", "documentacao", "readme", "guia", "guide",
        "manual", "changelog", "docs", "runbook",
    ];

    /// <summary>
    /// Sinais de que a demanda pede CONSTRUÇÃO, e não apenas decisão/investigação/documento.
    /// </summary>
    private static readonly string[] ImplementationTerms =
    [
        "implementar", "implement", "construir", "build", "codificar", "desenvolver", "develop",
        "criar endpoint", "criar api", "expor endpoint", "persistir", "migration", "corrigir bug",
        "refatorar", "refactor", "integrar com", "automatizar",
    ];

    private static readonly string[] DecisionTerms =
    [
        "decidir", "decisão", "decisao", "decision", "trade-off", "tradeoff", "escolher entre",
        "optar por", "definir estratégia", "definir estrategia", "aprovar direção", "aprovar direcao",
    ];

    public static DemandPlanProposal Plan(DemandDecompositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var featureId = ParseFeatureId(request.Title);
        var criteria = (request.AcceptanceCriteria ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        var subject = SubjectOf(request.Title);
        var hints = request.Hints;
        var haystack = BuildHaystack(request.Title, request.Description, criteria);

        // CERIMÔNIA PROPORCIONAL AO RISCO. A chefe já declara o risco de cada demanda; até aqui o
        // planner ignorava essa declaração e aplicava o mesmo rito a tudo. O resultado observado na
        // homologação: "mudar a cor do botão para verde" (risco `low`, declarado por ela) nascia
        // com card de documentação e GATE HUMANO — ou seja, uma troca de cor ficava parada
        // esperando decisão humana. Trabalho pequeno e reversível não paga esse pedágio: a revisão
        // independente do próprio card e o gate humano de MERGE continuam valendo, então nada de
        // segurança se perde ao dispensar o rito extra.
        var ceremonial = !string.Equals(request.RiskTier, "low", StringComparison.OrdinalIgnoreCase);

        var hasFrontend = hints?.HasFrontendSurface ?? MentionsAny(haystack, FrontendTerms);
        var needsCredential = hints?.RequiresExternalCredential ?? MentionsAny(haystack, ExternalCredentialTerms);
        var hasUncertainty = ceremonial && (hints?.HasTechnicalUncertainty ?? MentionsAny(haystack, UncertaintyTerms));
        var needsDecision = ceremonial && (hints?.RequiresDecision ?? MentionsAny(haystack, DecisionTerms));
        var hasDocumentation = ceremonial && MentionsAny(haystack, DocumentationTerms);

        // A fatia de backend deixou de ser incondicional. Uma demanda cujo entregável é uma DECISÃO
        // (ADR), uma INVESTIGAÇÃO ou um DOCUMENTO não produz código de servidor: emitir a fatia
        // assim mesmo colocava um agente para escrever código de produção do que ainda não foi
        // decidido — trabalho errado, cota gasta e ruído no board. O chefe pode declarar a natureza
        // da demanda pelo hint; sem hint, vale a leitura do texto: só se pede backend quando a
        // demanda menciona construir algo OU quando não é claramente decisão/investigação/doc.
        // ATENÇÃO ao que NÃO entra aqui: incerteza técnica não elimina a fatia de backend. Um
        // spike PRECEDE a construção — "investigar e depois implementar" é o caso normal, e tratar
        // incerteza como "não é código" apagaria a implementação de quase toda demanda real.
        // Só o entregável DECISÃO (ADR) ou DOCUMENTO, sem nenhum sinal de construção, dispensa a
        // fatia de servidor.

        var mentionsImplementation = MentionsAny(haystack, ImplementationTerms);
        var deliverableIsNotCode =
            (needsDecision || hasDocumentation) && !mentionsImplementation;
        var hasBackend = hints?.HasImplementationSurface ?? !deliverableIsNotCode;

        var ordinal = 0;
        var cards = new List<ProposedCard>();
        string Code() => $"{featureId}/T{++ordinal:00}";

        // Códigos dos gates/pré-requisitos que os cards de implementação passam a depender.
        var prerequisiteCodes = new List<string>();
        // Códigos dos cards de implementação dos quais a integração/crítica final depende.
        var implementationCodes = new List<string>();

        if (hasUncertainty)
        {
            var code = Code();
            prerequisiteCodes.Add(code);
            cards.Add(new ProposedCard(
                Title(code, "Spike: resolver incerteza técnica"),
                CardTypeSpike,
                RoleNone,
                $"Investigar a incerteza técnica de {featureId} e produzir uma recomendação. Não implementar a solução final; o resultado é uma decisão/registro que destrava os cards de implementação.",
                $"Investigação técnica delimitada de {featureId}: opções, riscos e recomendação.",
                "Implementação de produção; qualquer código que não seja descartável de prova de conceito.",
                criteria.Length > 0
                    ? [.. criteria.Select(c => $"A recomendação endereça: {c}")]
                    : ["Uma recomendação registrada permite escrever os critérios de aceite dos cards de implementação."],
                [],
                []));
        }

        if (needsCredential)
        {
            var code = Code();
            prerequisiteCodes.Add(code);
            cards.Add(new ProposedCard(
                Title(code, "Gate humano: provisionar credencial/homologação externa"),
                CardTypeHumanGate,
                RoleNone,
                $"Um humano precisa provisionar a credencial externa / concluir a homologação exigida por {featureId} antes de qualquer execução de agente. Registrar a referência opaca do segredo (nunca o valor).",
                "Provisionamento de credencial/homologação externa por um humano autorizado.",
                "Qualquer implementação de agente; o card permanece um bloqueador externo até o humano concluir.",
                ["A credencial/homologação externa está provisionada e referenciada por identificador opaco (sem segredo em claro)."],
                [],
                []));
        }

        if (needsDecision)
        {
            var code = Code();
            prerequisiteCodes.Add(code);
            cards.Add(new ProposedCard(
                Title(code, "Decisão: definir a abordagem"),
                CardTypeDecision,
                RoleNone,
                $"Uma decisão humana é necessária para {featureId}: escolher entre as alternativas em aberto. Registrar a decisão e sua justificativa antes de implementar.",
                "Escolha explícita entre alternativas mutuamente exclusivas, registrada com justificativa.",
                "Implementação; a decisão apenas destrava e direciona os cards de implementação.",
                ["A decisão está registrada com justificativa e destrava a implementação."],
                [],
                []));
        }

        // 4. Fatia de backend — presente quando a demanda produz código de servidor.
        if (hasBackend)
        {
            var backendCode = Code();
            implementationCodes.Add(backendCode);
            cards.Add(new ProposedCard(
                Title(backendCode, Label("Backend", subject, "Backend: implementar a fatia de servidor")),
                CardTypeAgentTask,
                RoleBackend,
                $"Implementar a fatia de backend de {featureId}: contratos tipados, tenant scope, OCC, cancellation e persistência dual quando aplicável, com testes proporcionais ao risco.",
                $"Código de servidor de {featureId}: domínio, aplicação, persistência, endpoints e testes de backend.",
                "Qualquer UI/frontend; provisionamento de credencial externa; documentação de produto.",
                ImplementationCriteria(criteria, "backend"),
                ["build", "tests"],
                [.. prerequisiteCodes]));
        }

        if (hasFrontend)
        {
            var frontendCode = Code();
            implementationCodes.Add(frontendCode);
            cards.Add(new ProposedCard(
                Title(frontendCode, Label("Frontend", subject, "Frontend: implementar a fatia de interface")),
                CardTypeAgentTask,
                RoleFrontend,
                $"Implementar a fatia de frontend de {featureId} contra os contratos publicados, com validação de UI e testes proporcionais.",
                $"Código de interface de {featureId}: componentes, telas e integração com os contratos do backend.",
                "Lógica de servidor/persistência; provisionamento de credencial externa.",
                ImplementationCriteria(criteria, "frontend"),
                ["build", "lint"],
                [.. prerequisiteCodes]));
        }

        if (hasDocumentation)
        {
            var docCode = Code();
            implementationCodes.Add(docCode);
            cards.Add(new ProposedCard(
                Title(docCode, Label("Documentação", subject, "Documentação: atualizar a documentação viva")),
                CardTypeAgentTask,
                RoleNone,
                $"Atualizar a documentação viva de {featureId} (guias, README, changelog) para refletir o comportamento entregue.",
                $"Documentação de {featureId}: guias de uso, notas de release e referências afetadas.",
                "Código de produção; a documentação apenas descreve o que foi implementado.",
                ["A documentação viva reflete o comportamento entregue e passa nos gates de documentação."],
                ["docs"],
                [.. prerequisiteCodes]));
        }

        // 7. Integração final — sempre presente, depende de todos os cards de implementação.
        //
        // É um GATE HUMANO, não um card de agente. Dois motivos, ambos canônicos: (a) a revisão
        // independente por agente crítico já acontece EM CADA card de implementação (ator≠crítico),
        // então um card extra de crítica duplicaria o que já foi feito; (b) a integração é o merge,
        // e o merge é gate humano por regra. Enquanto isto nascia como 'agent_task' com papel
        // 'critic', o card era ESTRUTURALMENTE indespachável — o papel crítico não possui escopo de
        // escrita, então todo ciclo do loop tentava despachá-lo e colhia `agent_path_scope_empty`,
        // para sempre, sem nunca escalar para um humano.
        // Demanda de risco baixo não recebe o gate de integração: ela tem uma fatia só, cuja
        // revisão independente e cujo merge humano já cobrem a entrega. O gate extra apenas
        // deixaria trabalho trivial parado esperando alguém clicar.
        if (!ceremonial)
        {
            return new DemandPlanProposal(featureId, cards);
        }

        var integrationCode = Code();
        cards.Add(new ProposedCard(
            Title(integrationCode, Label("Gate humano — integrar", subject, "Gate humano: revisar e integrar a feature")),
            CardTypeHumanGate,
            RoleNone,
            $"Revisar de forma independente e integrar as fatias de {featureId}: confirmar que os cards de implementação estão coerentes entre si, que os gates estão verdes e que os critérios de aceite da demanda foram atendidos ponta a ponta.",
            $"Revisão independente e integração ponta a ponta de {featureId}.",
            "Nova implementação de escopo; o card apenas integra e valida o que os cards de implementação entregaram.",
            criteria.Length > 0
                ? [.. criteria]
                : ["A feature está integrada e verificável ponta a ponta com gates verdes."],
            ["build", "tests", "review"],
            [.. implementationCodes]));

        return new DemandPlanProposal(featureId, cards);
    }

    private static string[] ImplementationCriteria(string[] criteria, string surface) =>
        criteria.Length > 0
            ? [.. criteria]
            : [$"O resultado da fatia de {surface} é verificável por teste automatizado."];

    private static string Title(string code, string label) => $"{code} {label}";

    /// <summary>
    /// ASSUNTO da demanda para compor o título do card. Os títulos eram rótulos fixos ("Backend:
    /// implementar a fatia de servidor"), então um board com várias demandas virava uma lista de
    /// cards indistinguíveis: o raciocínio do chefe chegava à demanda e morria ali, sem chegar ao
    /// card que o executor lê. Aqui o assunto real da demanda entra no título.
    ///
    /// O prefixo redundante ("ADR:", "Spike:", "Feature:") é removido porque o tipo do card já
    /// carrega essa informação; sobra o que identifica a demanda. Sem assunto utilizável, o rótulo
    /// genérico continua valendo — nunca um título vazio.
    /// </summary>
    private static string SubjectOf(string? demandTitle)
    {
        if (string.IsNullOrWhiteSpace(demandTitle))
        {
            return string.Empty;
        }

        var subject = FeatureIdPattern.Replace(demandTitle, string.Empty).Trim();
        var separator = subject.IndexOf(':');
        if (separator > 0 && separator < 24)
        {
            subject = subject[(separator + 1)..].Trim();
        }

        subject = subject.Trim(' ', '-', '—', '.', ':');
        return subject.Length is > 3 and <= 90 ? subject : string.Empty;
    }

    /// <summary>Rótulo do card: o assunto da demanda quando existe; o genérico quando não.</summary>
    private static string Label(string prefix, string subject, string fallback) =>
        subject.Length > 0 ? $"{prefix}: {subject}" : fallback;

    /// <summary>Extrai o código estável do card ("&lt;featureId&gt;/T&lt;nn&gt;") do título proposto.</summary>
    public static string CodeOf(string proposedTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposedTitle);
        var space = proposedTitle.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? proposedTitle : proposedTitle[..space];
    }

    private static string ParseFeatureId(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return FallbackFeatureId;
        }

        var match = FeatureIdPattern.Match(title);
        return match.Success
            ? match.Value.ToUpperInvariant()
            : FallbackFeatureId;
    }

    private static string BuildHaystack(string? title, string? description, IReadOnlyList<string> criteria)
    {
        var parts = new List<string> { title ?? string.Empty, description ?? string.Empty };
        parts.AddRange(criteria);
        // Espaços nas bordas permitem casar termos delimitados por espaço (ex.: " ui ").
        return $" {string.Join(" \n ", parts)} ".ToLower(CultureInfo.InvariantCulture);
    }

    private static bool MentionsAny(string haystack, string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));
}
