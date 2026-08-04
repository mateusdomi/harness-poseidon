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
    DemandDecompositionHints? Hints = null,

    /// <summary>
    /// A especialidade (chave de persona do catálogo) que o Chefe declarou para a demanda. Vai
    /// apenas aos cards que um profissional executa; cards de gate/decisão não têm executor.
    /// Um spike é pesquisa delegável e pode ter a especialidade inferida pelo despacho.
    /// Nulo mantém a inferência por texto no despacho.
    /// </summary>
    string? Specialty = null);

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
    bool? HasImplementationSurface = null,

    /// <summary>
    /// A decisão que falta é ARQUITETURAL — escolher a tecnologia concreta, o formato de
    /// persistência, a plataforma. Diferente de <see cref="RequiresDecision"/>, que é decisão de
    /// negócio/escopo e pertence ao humano: esta é trabalho delegável de análise e registro, e
    /// pertence ao Arquiteto.
    /// </summary>
    bool? RequiresArchitectureDecision = null);

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
    IReadOnlyList<string> Dependencies,

    /// <summary>Especialidade declarada pelo Chefe para este card, quando houver executor.</summary>
    string? Specialty = null,

    /// <summary>
    /// Fase 1B: o ORÇAMENTO do card — quantos agentes, quantos tokens, quantas rodadas e que
    /// profundidade de revisão. Determinístico por tipo × risco × natureza (<see cref="EffortPolicy"/>).
    ///
    /// Sem ele gravado aqui, a política existia e não governava nada: o despacho decidia esforço
    /// por hábito, e a tendência de um orquestrador sem regra é sempre a mesma — mais agentes, mais
    /// rodadas, revisão mais funda, como se esforço fosse gratuito e proporcional à qualidade.
    /// </summary>
    EffortBudget? Budget = null);

/// <summary>Plano proposto: o id da feature e a lista ORDENADA de cards filhos.</summary>
public sealed record DemandPlanProposal(string FeatureId, IReadOnlyList<ProposedCard> Cards);

/// <summary>
/// Planner PURO e determinístico que decompõe uma demanda canônica em cards filhos ATÔMICOS,
/// seguindo a política de criação de cards do dono do produto. Espelha a forma dos avaliadores puros
/// (<c>CardReadinessEvaluator</c>, <c>ReadinessEvaluator</c>): sem IO, sem relógio, sem aleatoriedade
/// — a mesma entrada produz sempre a mesma saída.
///
/// Regras (nesta ordem de emissão, códigos T01..Tnn):
/// 1. incerteza técnica (palavras-chave/dica) → um card 'spike' de pesquisa delegável;
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

    /// <summary>
    /// Registro de decisão arquitetural. É DESPACHÁVEL (ver <c>CardReadinessEvaluator</c>): um ADR
    /// é trabalho de análise e registro que o Arquiteto executa, ao contrário de
    /// <see cref="CardTypeDecision"/>, que é a decisão de negócio reservada ao humano.
    /// </summary>
    public const string CardTypeAdr = "adr";

    /// <summary>Persona do catálogo que responde por decisão de arquitetura.</summary>
    public const string SpecialtyArchitect = "playbook-arquiteto";

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

    /// <summary>
    /// Sinais de que a demanda mexe no SERVIDOR. Existem para distinguir uma demanda puramente
    /// visual de uma que também precisa de backend — sem eles, "mudar a cor do botão" e "propor
    /// um redesign da tela" nasciam com card de servidor junto, mandando um agente escrever
    /// código de produção que ninguém pediu.
    /// </summary>
    private static readonly string[] BackendTerms =
    [
        "backend", "back-end", "servidor", "server", "api", "endpoint", "banco", "database",
        "persist", "migration", "schema", "consulta", "query", "regra de negócio",
        "regra de negocio", "cálculo", "calculo", "autenticação", "autenticacao", "autorização",
        "autorizacao", "integração", "integracao", "importação", "importacao", "sincroniza",
        "backup", "job", "rotina", "fila", "webhook",
    ];

    private static readonly string[] DecisionTerms =
    [
        "decidir", "decisão", "decisao", "decision", "trade-off", "tradeoff", "escolher entre",
        "optar por", "definir estratégia", "definir estrategia", "aprovar direção", "aprovar direcao",
    ];

    /// <summary>
    /// Sinais de que o que falta decidir é a TECNOLOGIA CONCRETA, não o escopo. Uma decisão dessas
    /// não é do humano: é card do Arquiteto, que a analisa e a registra num ADR.
    /// </summary>
    private static readonly string[] ArchitectureDecisionTerms =
    [
        "stack", "tecnologia concreta", "tecnologia a usar", "linguagem e framework",
        "escolha de framework", "escolha de banco", "banco a usar", "plataforma alvo",
        "adr de stack", "definir a stack", "decidir a stack",
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
        var subjectOrFeature = subject.Length > 0 ? subject : featureId;
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

        // A decisão ARQUITETURAL não passa pelo pedágio de `ceremonial`. Escrever código sem a
        // tecnologia decidida não é rito extra evitável: é a diferença entre um card executável e
        // um card que o ator recusa — e ele recusa com razão, porque o plano aceito declara a
        // decisão como pré-requisito. Medido nesta operação em 04/08/2026: quatro tentativas
        // seguidas, ~38 mil tokens, zero commit, todas reprovadas por "diff vazio", enquanto o
        // transcript do ator dizia "fabricar uma stack aqui usurparia a decisão do Arquiteto".
        var needsArchitectureDecision =
            hints?.RequiresArchitectureDecision ?? MentionsAny(haystack, ArchitectureDecisionTerms);
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
        // Demanda puramente VISUAL não gera fatia de servidor. A linha de base continua sendo o
        // backend — uma demanda genérica, sem superfície declarada, segue recebendo a fatia —, mas
        // quando o texto fala só de tela e não menciona nada de servidor, emitir o card de backend
        // é trabalho errado: alguém vai executá-lo e gastar cota escrevendo o que ninguém pediu.
        // A conclusão "é só visual" exige EVIDÊNCIA NO TEXTO de que a demanda é de tela. O hint
        // `hasFrontendSurface` não serve para isso: ele afirma que EXISTE superfície de frontend,
        // e não diz nada sobre a ausência de backend. Usá-lo aqui apagava a fatia de servidor de
        // demandas que só declaravam ter tela — inclusive as que precisavam das duas.
        var mentionsBackend = MentionsAny(haystack, BackendTerms);
        var visualOnly = MentionsAny(haystack, FrontendTerms) && !mentionsBackend;
        var hasBackend = hints?.HasImplementationSurface ?? (!deliverableIsNotCode && !visualOnly);

        // A especialidade declarada pelo Chefe só acompanha cards que um AGENTE executa. Um gate
        // humano, um spike ou uma decisão não têm persona executora: carimbá-los sugeriria um dono
        // que o card não tem.
        var specialty = string.IsNullOrWhiteSpace(request.Specialty) ? null : request.Specialty.Trim();

        var ordinal = 0;
        var cards = new List<ProposedCard>();
        string Code() => $"{featureId}/T{++ordinal:00}";

        // Códigos dos gates/pré-requisitos que os cards de implementação passam a depender.
        var prerequisiteCodes = new List<string>();
        // Códigos dos cards de implementação dos quais a integração/crítica final depende.
        var implementationCodes = new List<string>();

        // A decisão de arquitetura vem ANTES de tudo: é dela que os demais cards dependem, e o
        // código estável do card ("T01") reflete essa ordem. Só existe quando o plano de fato
        // produz código — decidir a stack de um entregável que é documento não faz sentido.
        if (needsArchitectureDecision && (hasBackend || hasFrontend))
        {
            var code = Code();
            prerequisiteCodes.Add(code);
            cards.Add(new ProposedCard(
                Title(code, "ADR: decidir e registrar a stack concreta"),
                // `adr` e não `decision`: o segundo é humano e NÃO despachável, então um plano que
                // o emitisse deixaria a implementação esperando para sempre por um card que agente
                // nenhum executa. Esse era o dead-end — as duas metades (criar a decisão e segurar
                // a implementação) andam juntas ou nenhuma anda.
                CardTypeAdr,
                RoleBackend,
                $"Decidir e registrar, em um ADR novo, a tecnologia concreta que realiza a forma " +
                    $"arquitetural já aceita para {subjectOrFeature}: linguagem, framework, " +
                    "formato de persistência e plataforma de execução. O ADR é o entregável — " +
                    "não implemente a demanda neste card.",
                $"Um ADR em docs/decisions/ que fixa a stack concreta de {featureId}, com " +
                    "alternativas consideradas e justificativa.",
                "Qualquer implementação da demanda; a decisão apenas destrava os cards de código.",
                [
                    "O ADR registra a tecnologia concreta escolhida (linguagem, framework, persistência e plataforma) com justificativa.",
                    "O ADR não reabre decisões de arquitetura já aceitas em artefatos anteriores; escolhe dentro delas.",
                ],
                [],
                [],
                SpecialtyArchitect));
        }

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
                // A instrução descreve A DEMANDA, não a arquitetura de um projeto específico. O texto
                // anterior mandava aplicar "tenant scope, OCC e persistência dual" em QUALQUER card
                // — inclusive numa CLI Python de um projeto-cliente, onde nada disso existe. Ruído
                // no melhor caso; instrução enganosa no pior. Os padrões do projeto o executor
                // encontra no próprio repositório e no contexto governado.
                $"Implementar a fatia de servidor de {subjectOrFeature}. Siga os padrões já " +
                    "estabelecidos no repositório do projeto e escreva testes proporcionais ao risco.",
                $"Código de servidor de {featureId}: domínio, aplicação, persistência, endpoints e testes de backend.",
                "Qualquer UI/frontend; provisionamento de credencial externa; documentação de produto.",
                ImplementationCriteria(criteria, "backend"),
                ["build", "tests"],
                [.. prerequisiteCodes],
                specialty));
        }

        if (hasFrontend)
        {
            var frontendCode = Code();
            implementationCodes.Add(frontendCode);
            cards.Add(new ProposedCard(
                Title(frontendCode, Label("Frontend", subject, "Frontend: implementar a fatia de interface")),
                CardTypeAgentTask,
                RoleFrontend,
                $"Implementar a fatia de interface de {subjectOrFeature} contra os contratos publicados, com validação de UI e testes proporcionais.",
                $"Código de interface de {featureId}: componentes, telas e integração com os contratos do backend.",
                "Lógica de servidor/persistência; provisionamento de credencial externa.",
                ImplementationCriteria(criteria, "frontend"),
                ["build", "lint"],
                [.. prerequisiteCodes],
                specialty));
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
                [.. prerequisiteCodes],
                specialty));
        }

        // 6b. GARANTIA DE ENTREGÁVEL. Spike investiga, decisão escolhe, gate de credencial
        // provisiona e gate de integração verifica — nenhum deles PRODUZ o que o usuário pediu.
        // Enquanto a fatia de servidor era a linha de base incondicional, sempre havia alguém
        // encarregado do resultado; agora que a superfície pode ser negada (pelo texto ou pela
        // declaração do chefe), uma demanda cujo entregável não é código de servidor, nem
        // interface, nem documentação cerimonial ficava com um plano SEM NENHUM produtor: um
        // threat model, um plano de testes ou uma análise nasciam com gates que esperariam para
        // sempre por cards que não existem. Aqui o plano volta a garantir um dono do entregável;
        // o papel fica em aberto e é inferido no despacho, então isto não reintroduz "tudo é
        // backend".
        if (implementationCodes.Count == 0)
        {
            var deliverableCode = Code();
            implementationCodes.Add(deliverableCode);
            cards.Add(new ProposedCard(
                Title(deliverableCode, Label("Entregável", subject, "Produzir o entregável da demanda")),
                CardTypeAgentTask,
                RoleNone,
                $"Produzir o entregável de {subjectOrFeature} conforme descrito na demanda, com o conteúdo real (nunca um esqueleto) e as evidências que os critérios de aceite exigem.",
                $"O entregável descrito na demanda {featureId}.",
                "Decisões que dependem de humano; provisionamento de credencial externa; trabalho não pedido pela demanda.",
                criteria.Length > 0
                    ? [.. criteria]
                    : ["O entregável descrito na demanda existe e é verificável."],
                [],
                [.. prerequisiteCodes],
                specialty));
        }

        // 7. NÃO existe mais um gate humano de integração por demanda.
        //
        // Ele nasceu para garantir que alguém conferisse a feature ponta a ponta, e virou o
        // pedágio mais caro do produto: a revisão independente já acontece EM CADA card de
        // implementação (ator≠crítico), o merge do card revisado passou a ser feito pela própria
        // chefe, e a verificação ponta a ponta da entrega passou a ser a obrigação da FASE, medida
        // pelo plano de obrigações e decidida no portão conforme o modo do projeto. Manter o card
        // aqui só acrescentaria um item indespachável ao board de cada demanda, esperando um
        // clique que não decide mais nada — e o dono do projeto é o stakeholder, não o operador
        // que fecha feature por feature.
        //
        // O que continua sendo gate humano nesta decomposição é o que exige o mundo externo:
        // provisionar credencial (acima) e escolher entre alternativas mutuamente exclusivas.
        // B11/F14 — fronteira negativa: cada card recebe, no próprio enunciado, o que pertence aos
        // IRMÃOS. Sem isso, um agente que encontra um defeito real fora do próprio escopo tende a
        // consertá-lo — comportamento louvável isolado, e destrutivo em paralelo: dois agentes
        // editam o mesmo arquivo, o merge serializado vira conflito e o trabalho do outro é
        // sobrescrito. Declarar o limite é mais barato que arbitrar a colisão depois.
        // Fase 1B: o ORÇAMENTO entra no card aqui, no fim da decomposição, quando já se sabe o
        // tipo, o papel e quantos irmãos ele tem. A política é determinística: a mesma demanda
        // produz sempre os mesmos orçamentos, e sem isso ninguém consegue dizer se um resultado
        // melhorou pelo plano ou pela sorte.
        var risk = ParseRiskTier(request.RiskTier);
        var isSmall = cards.Count <= 2;
        var budgeted = cards
            .Select(card => card with { Budget = EffortPolicy.Decide(NatureOf(card), risk, isSmall) })
            .ToList();
        return NegativeBoundaryPolicy.EnrichWithSiblingBoundaries(
            new DemandPlanProposal(featureId, budgeted));
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

    /// <summary>
    /// A natureza do card a partir do que ele É, não do texto da demanda. Um spike entrega
    /// conhecimento; um gate humano e uma decisão não têm executor; frontend é verificável olhando;
    /// e um card de implementação que mexe em contrato é estrutural.
    /// </summary>
    internal static WorkNature NatureOf(ProposedCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (string.Equals(card.CardType, CardTypeSpike, StringComparison.Ordinal))
        {
            return WorkNature.Investigation;
        }

        if (string.Equals(card.CardType, CardTypeDecision, StringComparison.Ordinal) ||
            string.Equals(card.CardType, CardTypeHumanGate, StringComparison.Ordinal))
        {
            // Sem executor de agente: o orçamento existe para não deixar o card sem regra, e o
            // menor possível é o honesto — quem trabalha aqui é gente.
            return WorkNature.Investigation;
        }

        if (string.Equals(card.RequiredRole, RoleFrontend, StringComparison.Ordinal))
        {
            return WorkNature.Visual;
        }

        // Backend que mexe em contrato, dado persistido ou fronteira entre módulos é estrutural —
        // é o caso em que revisar mais fundo se paga.
        return MentionsAny(
                $"{card.Instruction} {card.InScope}".ToLowerInvariant(), StructuralTerms)
            ? WorkNature.Structural
            : WorkNature.Implementation;
    }

    /// <summary>Termos que denunciam mudança estrutural: contrato, dado persistido, fronteira.</summary>
    private static readonly string[] StructuralTerms =
    [
        "contrato", "schema", "migration", "migração", "banco", "tabela", "api pública",
        "breaking", "endpoint", "protocolo", "interface pública", "persistência",
    ];

    private static RiskTier ParseRiskTier(string? priority) => priority?.Trim().ToLowerInvariant() switch
    {
        "critical" => RiskTier.Critical,
        "high" => RiskTier.High,
        "low" => RiskTier.Low,
        _ => RiskTier.Medium,
    };

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
