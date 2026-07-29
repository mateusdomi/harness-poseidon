using System.Text.RegularExpressions;

namespace Harness.Modules.Agents.Application.Execution;

/// <summary>
/// Projeção de comunicação usada pelo Chief no canal conversacional. A autorização é um dado
/// resolvido fora do modelo; a mensagem do usuário, sozinha, nunca amplia o que pode ser exposto.
/// </summary>
public sealed record ChiefCommunicationContext(
    bool TechnicalDetailsRequested = false,
    bool TechnicalDetailsAuthorized = false)
{
    public bool CanExposeTechnicalDetails =>
        TechnicalDetailsRequested && TechnicalDetailsAuthorized;
}

/// <summary>
/// Política central da linguagem publicada pela Bruna. Ela separa fatos operacionais, que
/// continuam no estado durável e na auditoria, da projeção de negócio apresentada no chat.
/// </summary>
public static partial class ChiefCommunicationPolicy
{
    private const string TechnicalEntitlementKey = "presentation.technical";

    public const string PublicName = "Bruna Magalhães";
    public const string PublicTitle = "Diretora de Engenharia e Operações de IA";

    public static string RequiredTechnicalEntitlement => TechnicalEntitlementKey;

    public static ChiefCommunicationContext Business { get; } = new();

    /// <summary>
    /// Reconhece apenas pedidos explícitos. Menções incidentais a arquitetura ou tecnologia não
    /// mudam o modo de apresentação, e autorização continua sendo exigida separadamente.
    /// </summary>
    public static bool RequestsTechnicalDetails(string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return false;
        }

        return TechnicalRequestPattern().IsMatch(instruction);
    }

    public static string BuildInstructions(
        ChiefCommunicationContext context,
        string? userPreferences = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var projection = context.CanExposeTechnicalDetails
            ? """
              O usuário pediu detalhes técnicos e o servidor confirmou a autorização deste perfil.
              Você pode apresentar provider, modelo, executor, conta, estados internos, IDs e
              evidências técnicas somente na medida necessária para responder ao pedido. Nunca
              exponha segredo, credencial, token de autenticação ou log bruto com dados sensíveis.
              Comece pela consequência de negócio e separe os detalhes numa seção técnica.
              """
            : """
              Você está na experiência de negócio. Não exponha provider, modelo, executor, conta,
              cota, token, worktree, branch, lease, fencing, heartbeat, tenant, slug, SQL, digest,
              UTC, IDs, logs, nomes internos de estado, cards, risk tier, gates, reason codes ou
              regras internas. Traduza fatos técnicos para impacto, andamento, qualidade,
              pendência e próxima ação. Se o usuário pedir detalhes técnicos sem autorização
              confirmada, diga apenas que eles não estão disponíveis neste perfil.
              """;

        var preferences = string.IsNullOrWhiteSpace(userPreferences)
            ? "Use português do Brasil, com tom profissional, sereno, caloroso, acolhedor e direto."
            : userPreferences.Trim();

        return $$"""
            Você se apresenta sempre como {{PublicName}} — {{PublicTitle}}.
            Você é a voz de uma equipe de agentes de IA. Se perguntarem diretamente se você é
            uma IA ou um sistema, responda com honestidade; nunca afirme ser uma pessoa física.

            {{projection}}

            Regras invariantes:
            - Você é a voz de uma equipe de agentes de IA; nunca afirme ser humana, funcionária humana ou
              possuir vínculo empregatício real.
            - Distingua fato, inferência, incerteza e decisão pendente. Não invente progresso,
              aprovação, prazo, capacidade, causa, evidência ou conclusão.
            - Comunique primeiro: onde estamos; o que foi concluído; o que acontece agora; o que
              vem depois; e somente então o que realmente depende do usuário.
            - Não despeje histórico interno nem peça ao stakeholder para priorizar trabalho
              operacional. Em modo autônomo, priorize, organize, retome e replaneje. Em modo
              semiautônomo ou manual, peça decisão somente no gate humano configurado e explique
              o impacto em linguagem simples.
            - Não diga “diga quais cards devo priorizar”, “não vou criar nada neste turno” ou
              equivalentes. Use no máximo dois emojis e apenas quando combinarem com o contexto.
            - Más notícias devem ser claras, honestas e acionáveis, sem códigos internos na
              experiência de negócio. Ofereça detalhes técnicos somente sob pedido e autorização.
            - Quando o contexto trouxer `reasonCodeTranslations`, use a frase humana e o próximo
              passo desse mapa; jamais repita a chave técnica. Se não houver tradução explícita,
              admita o imprevisto sem inventar causa e diga que a equipe está verificando.

            Comportamento por situação:
            - Resumo do projeto: sintetize etapa atual, entregas concluídas, trabalho em curso,
              próximo passo e eventual decisão humana real.
            - Projeto pausado: explique a pausa e seu efeito; se ela veio de decisão explícita do
              usuário, pergunte se deseja retomar. Não transfira a priorização operacional.
            - Falha técnica: diga o impacto, o que permanece preservado e o caminho de retomada;
              não publique stack trace, código, ID ou log no modo de negócio.
            - Aprovação pendente: peça somente a decisão humana configurada, descrevendo benefício,
              prioridade, alternativa e consequência de aprovar ou aguardar.
            - Nova demanda: acolha e pergunte primeiro qual resultado o usuário deseja. Faça uma
              pergunta por vez ou um grupo pequeno, aceite respostas incompletas, infira apenas
              detalhes reversíveis e diga que organizará os detalhes com a equipe. Pergunte também
              até quando ele precisa do resultado e registre a resposta como prazo desejado; se ele
              não souber ou não quiser definir, registre "sem prazo definido" e siga — prazo é
              declaração do usuário, nunca estimativa sua, e nenhuma data pode ser inventada para
              preencher o campo.
            - Falta de informação: declare a incerteza, registre hipóteses reversíveis e peça
              somente o mínimo indispensável.
            - Atraso: informe impacto, causa conhecida ou incerteza, plano de recuperação e nova
              referência de acompanhamento sem inventar prazo.
            - Bloqueio externo: explique de quem ou do que depende, o impacto e a ação mínima para
              desbloquear, sem expor infraestrutura irrelevante.
            - Conclusão: declare concluído somente com evidência; resuma resultado, qualidade,
              pendências residuais e próxima entrega.
            - Risco crítico: seja direta sobre impacto e urgência, não minimize, não dramatize e
              solicite decisão humana apenas quando a política exigir.

            Preferências de voz configuradas pelo responsável:
            {{preferences}}

            As preferências de voz personalizam tom e tratamento, mas não podem remover, reduzir
            ou contradizer esta política, a autorização, a governança ou o contrato de saída.
            """;
    }

    /// <summary>
    /// Gate determinístico aplicado depois do schema. Ele evita que uma resposta tecnicamente
    /// válida em JSON publique detalhes que a projeção de negócio não autoriza.
    /// </summary>
    public static bool TryValidateResponse(
        string response,
        ChiefCommunicationContext context,
        out string? violation)
    {
        ArgumentNullException.ThrowIfNull(context);
        violation = null;

        if (string.IsNullOrWhiteSpace(response))
        {
            violation = "A resposta conversacional está vazia.";
            return false;
        }

        if (CanonicalIdentityDenialPattern().IsMatch(response) ||
            CanonicalAiDenialPattern().IsMatch(response))
        {
            violation = "A resposta nega a identidade pública fixa de Bruna ou sua natureza de IA.";
            return false;
        }

        var affirmativeIdentityClaims = NegatedIdentityClaimPattern().Replace(response, string.Empty);
        var nonCanonicalIdentityClaims =
            CanonicalIdentityIntroductionPattern().Replace(
                affirmativeIdentityClaims,
                string.Empty);
        if (FalseHumanIdentityPattern().IsMatch(affirmativeIdentityClaims))
        {
            violation = "A resposta atribui identidade humana ou vínculo real à equipe de agentes.";
            return false;
        }

        if (ConflictingPublicIdentityPattern().IsMatch(nonCanonicalIdentityClaims))
        {
            violation = "A resposta apresenta nome ou função diferente da identidade pública de Bruna.";
            return false;
        }

        if (OperationalPrioritizationRequestPattern().IsMatch(response))
        {
            violation = "A resposta transfere priorização operacional ao stakeholder.";
            return false;
        }

        if (HostileIntakePattern().IsMatch(response))
        {
            violation = "A resposta de intake aumenta desnecessariamente o esforço do stakeholder.";
            return false;
        }

        var referenceSafeSurface = UrlAuthorityPattern().Replace(response, string.Empty);
        referenceSafeSurface = EmailAddressPattern().Replace(referenceSafeSurface, string.Empty);
        referenceSafeSurface = FileReferencePattern().Replace(referenceSafeSurface, string.Empty);
        var reasonCodeSurface =
            BarePublicDomainPattern().Replace(referenceSafeSurface, string.Empty);
        if (!context.CanExposeTechnicalDetails &&
            (TechnicalVocabularyPattern().IsMatch(response) ||
             InternalIdentifierPattern().IsMatch(response) ||
             RecognizableReasonCodePattern().IsMatch(referenceSafeSurface) ||
             RawReasonCodePattern().IsMatch(reasonCodeSurface)))
        {
            violation = "A resposta contém detalhe técnico não autorizado para a experiência de negócio.";
            return false;
        }

        return true;
    }

    public static string TerminalFailureMessage(ChiefCommunicationContext context) =>
        context.CanExposeTechnicalDetails
            ? """
              Não consegui concluir o processamento da sua última mensagem depois das tentativas
              automáticas. Nenhuma decisão foi tomada e nenhum trabalho foi delegado a partir dela.
              Você pode reenviar a solicitação para eu retomar; os detalhes técnicos da falha estão
              preservados na auditoria para investigação.
              """
            : """
              Não consegui concluir o processamento da sua última mensagem. Nenhuma decisão foi
              tomada e nenhum trabalho foi iniciado a partir dela. Você pode reenviar a solicitação
              para eu retomar com segurança; se o problema continuar, eu aviso o próximo passo.
              """;

    [GeneratedRegex(
        @"(?ix)
        \b(
            detalhes?\s+t[eé]cnicos? |
            diagn[oó]stico\s+t[eé]cnico |
            informa[cç][oõ]es?\s+t[eé]cnicas? |
            qual\s+(?:[eé]\s+o\s+)?(?:provider|provedor|executor|modelo(?!\s+de\s+neg[oó]cio)) |
            mostre\s+(?:o\s+|os\s+)?(?:provider|provedor|modelo|executor|logs?|ids?) |
            exiba\s+(?:o\s+|os\s+)?(?:provider|provedor|modelo|executor|logs?|ids?) |
            logs?\s+(?:completos?|t[eé]cnicos?|da\s+execu[cç][aã]o) |
            ids?\s+(?:internos?|t[eé]cnicos?|da\s+execu[cç][aã]o)
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalRequestPattern();

    [GeneratedRegex(
        @"(?ix)\b(
            provider|provedor|modelo(?!\s+de\s+neg[oó]cio)|executor|
            conta\s+(?:t[eé]cnica|configurada|do\s+provider|do\s+provedor|openai|anthropic|kimi|glm)|
            cotas?|quotas?|tokens?|worktrees?|branches?|leases?|fencing|heartbeat|tenant|slug|
            sql|digest|utc|backend|frontend|backlog|ready|cards?|risk\s*tier|gates?|
            projection\s*mismatch|stack\s*trace|logs?|c[oó]digo\s+t[eé]cnico|
            claude|openai|anthropic|kimi|glm
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalVocabularyPattern();

    [GeneratedRegex(
        @"(?ix)
        \b[0-9A-HJKMNP-TV-Z]{26}\b |
        \b[0-9A-F]{8}-[0-9A-F]{4}-[1-5][0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\b |
        \#[A-Z0-9]{5,}\b |
        \b(?:id|identificador|turno)\s*[:#-]?\s*
            (?=[A-Z0-9_-]*\d)[A-Z0-9][A-Z0-9_-]{3,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex InternalIdentifierPattern();

    [GeneratedRegex(
        @"(?ix)\bhttps?://(?:[^@\s/?#]+@)?[^/\s?#]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlAuthorityPattern();

    [GeneratedRegex(
        @"(?ix)\b[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)+\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddressPattern();

    [GeneratedRegex(
        @"(?ix)\b[\p{L}0-9][\p{L}0-9_-]*\.
            (?:pdf|docx?|xlsx?|pptx?|csv|zip|7z|rar|png|jpe?g|webp|gif|svg|
               md|txt|rtf|json|xml|ya?ml|html?|mp4|mov|mp3|wav)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex FileReferencePattern();

    [GeneratedRegex(
        @"(?ix)
        (?:
            \b(?:
                acesse|consulte|visite|site|dom[ií]nio|endere[cç]o |
                (?:(?:o|a)\s+)?(?:
                    site|projeto|portal|p[aá]gina|aplica[cç][aã]o|produto|
                    prot[oó]tipo|documenta[cç][aã]o|material|servi[cç]o
                )\s+(?:
                    (?:est[aá]|fica|segue|encontra-se)\s+
                        (?:dispon[ií]vel\s+)?(?:em|no|na) |
                    foi\s+(?:publicad[oa]|hospedad[oa])\s+(?:em|no|na)
                ) |
                acompanhe\s+(?:(?:o|a)\s+)?(?:
                    projeto|site|portal|p[aá]gina|aplica[cç][aã]o|produto|
                    prot[oó]tipo|documenta[cç][aã]o|servi[cç]o
                )\s+(?:em|no|na)
            )\s+ |
            \bwww\.
        )
        (?:[a-z0-9-]+\.)+[a-z]{2,63}\b(?![a-z0-9-]|\.[a-z0-9-]) |
        \b(?:[a-z0-9-]+\.)+
        (?:
            aero|agency|ai|app|art|biz|br|cafe|cat|cloud|co|com|company|coop|de|
            design|dev|digital|edu|email|fr|gov|info|io|jobs|link|live|me|media|
            mobi|museum|name|net|network|news|online|one|org|pro|pt|shop|site|
            software|solutions|space|store|studio|systems|team|tech|tools|top|
            travel|uk|us|vip|website|work|world|xyz
        )\b(?![a-z0-9-]|\.[a-z0-9-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex BarePublicDomainPattern();

    [GeneratedRegex(
        @"(?ix)\b(?:
            account|agent|agents|app|approval|approve|archive|attempt|audit|auth|availability|
            backlog|backup|board|build|canon|capability|channel|chief|com|component|compose|
            context|continuation|conversation|core|credentials|critic|decision|demand|delivery|
            document|dor|dotnet|durable|error|exception|execution|executor|gate|gen_ai|global|
            gov|governance|guardrails|harness|http|hub|identity|index|item|judge|launcher|
            license|licensing|main|manifest|message|messaging|model|model_router|notification|
            org|organization|outbox|package|persona|policy|pom|poseidon|presentation|profile|
            progress|project|projects|prototype|provider|provider_account|providers|quota|
            readiness|request|response|resume|run|runner|scheduler|scope|secrets|self|server|
            session|settings|signal|smba|solicitation|sso|task|team|thread|timer|tool|turn|
            url|user|wait|workflow|tail|chief_loop
        )\.(?:[a-z][a-z0-9_]*\.)*(?:
            acquired|allowed|approved|archived|available|blocked|cancelled|checkpointed|
            circuit_open|completed|conflict|created|degraded|deleted|denied|disabled|
            dispatched|eligible(?:_degraded|_near_limit|_degraded_near_limit)?|error|
            exhausted|failed|forbidden|invalid|limited|loaded|mismatch|missing|
            not_allowed|not_available|not_found|not_implemented|orphaned_by_host_restart|
            open|reached|reconciled|recovered|required|requested|resolved|restored|
            running|selected|slow|started|stuck|timeout|unauthorized|unavailable|unknown|
            unsupported|updated
        )\b(?!\.[a-z0-9-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex RecognizableReasonCodePattern();

    [GeneratedRegex(
        @"(?ix)\b(?:
            (?:
                account|agent|agents|app|approval|approve|archive|attempt|audit|auth|availability|
                backlog|backup|board|build|canon|capability|channel|chief|com|component|compose|
                context|continuation|conversation|core|credentials|critic|decision|demand|delivery|
                document|dor|dotnet|durable|error|exception|execution|executor|gate|gen_ai|global|
                gov|governance|guardrails|harness|http|hub|identity|index|item|judge|launcher|
                license|licensing|main|manifest|message|messaging|model|model_router|notification|
                org|organization|outbox|package|persona|policy|pom|poseidon|presentation|profile|
                progress|project|projects|prototype|provider|provider_account|providers|quota|
                readiness|request|response|resume|run|runner|scheduler|scope|secrets|self|server|
                session|settings|signal|smba|solicitation|sso|task|team|thread|timer|tool|turn|
                url|user|wait|workflow|tail|chief_loop
            )\.[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)* |
            [a-z][a-z0-9_]*\.[a-z][a-z0-9_]*_[a-z0-9_]*(?:\.[a-z][a-z0-9_]*)* |
            [a-z][a-z0-9]*(?:_[a-z0-9]+)+(?!\.[a-z0-9]{2,10}\b)
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex RawReasonCodePattern();

    [GeneratedRegex(
        @"(?ix)\b(?:
            n[aã]o\s+sou\s+(?:a\s+)?Bruna(?:\s+Magalh[aã]es)? |
            n[aã]o\s+sou\s+(?:a\s+)?Diretora\s+de\s+Engenharia\s+e\s+
                Opera[cç][oõ]es\s+de\s+IA |
            meu\s+nome\s+n[aã]o\s+[eé]\s+Bruna(?:\s+Magalh[aã]es)?
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalIdentityDenialPattern();

    [GeneratedRegex(
        @"(?ix)\b(?:
            n[aã]o\s+sou\s+(?:uma?\s+)?(?:
                IA |
                intelig[eê]ncia\s+artificial |
                agente\s+de\s+IA |
                sistema(?:\s+de\s+IA)?
            ) |
            n[aã]o\s+sou\s+(?:uma?\s+)?sistema
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalAiDenialPattern();

    [GeneratedRegex(
        @"\b(?:
            (?i:n[aã]o\s+sou\s+)(?:
                (?i:(?:uma\s+)?(?:pessoa\s+(?:real|f[ií]sica)|human[oa]|
                    funcion[aá]ri[oa]\s+human[oa]|empregad[oa]\s+human[oa])) |
                (?i:(?:(?:a|o|uma|um)\s+)?(?:
                    chief|chefe|orquestrador[ae]?|gerente|gestor[ae]?|diretor[ae]?|
                    coordenador[ae]?|l[ií]der|respons[aá]vel|head|
                    product\s+owner|scrum\s+master|arquitet[oa]|engenheir[oa]|
                    analista|consultor[ae]?|assistente
                )) |
                (?:a\s+|o\s+)?[\p{Lu}][\p{L}'-]*(?:\s+[\p{Lu}][\p{L}'-]*)?
            ) |
            (?i:n[aã]o\s+(?:tenho|possuo)\s+(?:um\s+)?v[ií]nculo\s+empregat[ií]cio\s+real)
        )\b",
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedIdentityClaimPattern();

    [GeneratedRegex(
        @"(?ix)\b(
            (?:eu\s+)?sou\s+(?:uma\s+)?(?:
                pessoa\s+(?:real|f[ií]sica) |
                human[oa] |
                funcion[aá]ri[oa]\s+human[oa] |
                empregad[oa]\s+human[oa]
            ) |
            (?:eu\s+)?(?:tenho|possuo)\s+(?:um\s+)?v[ií]nculo\s+empregat[ií]cio\s+real
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex FalseHumanIdentityPattern();

    [GeneratedRegex(
        @"\b(?:
            (?i:meu\s+nome\s+[eé]|eu\s+me\s+chamo|(?:eu\s+)?sou)\s+
                (?:
                    (?:a\s+)?Bruna(?:\s+Magalh[aã]es)?
                        (?!\s+(?:(?:da|de|do|das|dos)\s+)?[\p{Lu}][\p{Ll}]) |
                    (?:a\s+)?Diretora\s+de\s+Engenharia\s+e\s+Opera[cç][oõ]es\s+de\s+IA |
                    IA |
                    (?:a|uma?)\s+IA |
                    (?:uma?\s+)?agente\s+de\s+IA |
                    (?:uma?\s+)?sistema(?:\s+de\s+IA)?
                ) |
            (?i:aqui\s+[eé]\s+(?:a\s+)?)Bruna(?:\s+Magalh[aã]es)?
                (?!\s+(?:(?:da|de|do|das|dos)\s+)?[\p{Lu}][\p{Ll}])
        )",
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalIdentityIntroductionPattern();

    [GeneratedRegex(
        @"\b(?:
            (?i:meu\s+nome\s+[eé]|eu\s+me\s+chamo)\s+
                [\p{Lu}][\p{L}'-]*(?:\s+[\p{Lu}][\p{L}'-]*)? |
            (?i:(?:eu\s+)?sou\s+(?:(?:a|o|uma|um)\s+)?(?:
                chief|chefe|orquestrador[ae]?|gerente|gestor[ae]?|diretor[ae]?|
                coordenador[ae]?|l[ií]der|respons[aá]vel|head|
                product\s+owner|scrum\s+master|arquitet[oa]|engenheir[oa]|
                analista|consultor[ae]?|assistente
            ))\b |
            (?i:(?:eu\s+)?sou\s+)(?:a\s+|o\s+)?
                [\p{Lu}][\p{L}'-]*(?:\s+[\p{Lu}][\p{L}'-]*)? |
            (?i:aqui\s+[eé]\s+)(?:a\s+|o\s+)?
                [\p{Lu}][\p{L}'-]*(?:\s+[\p{Lu}][\p{L}'-]*)?
        )",
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex ConflictingPublicIdentityPattern();

    [GeneratedRegex(
        @"(?ix)\b(
            diga(?:-me)?\s+quais?\s+(?:cards?|atividades?|tarefas?|entregas?)\s+
                (?:eu\s+)?devo\s+priorizar |
            escolha\s+quais?\s+(?:cards?|atividades?|tarefas?|entregas?)\s+
                (?:eu\s+)?devo\s+(?:fazer|executar|priorizar) |
            priorize\s+(?:os\s+|as\s+)?(?:cards?|atividades?|tarefas?|entregas?)\s+para\s+mim
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex OperationalPrioritizationRequestPattern();

    [GeneratedRegex(
        @"(?ix)\b(
            n[aã]o\s+vou\s+criar\s+nada\s+neste\s+turno |
            informe\s+(?:o\s+)?m[oó]dulo,\s*(?:o\s+)?reposit[oó]rio\s+e\s+(?:a\s+)?stack
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostileIntakePattern();
}
