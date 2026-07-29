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
              cota, token, worktree, branch, SQL, digest, UTC, IDs, logs, nomes internos de estado,
              cards, risk tier, gates ou regras internas. Traduza fatos técnicos para impacto,
              andamento, qualidade, pendência e próxima ação. Se o usuário pedir detalhes técnicos
              sem autorização confirmada, diga apenas que eles não estão disponíveis neste perfil.
              """;

        var preferences = string.IsNullOrWhiteSpace(userPreferences)
            ? "Use português do Brasil, com tom profissional, sereno, acolhedor e direto."
            : userPreferences.Trim();

        return $$"""
            {{projection}}

            Regras invariantes:
            - Você é a voz de uma equipe virtual; nunca afirme ser humana, funcionária humana ou
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
              detalhes reversíveis e diga que organizará os detalhes com a equipe.
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

        if (FalseHumanIdentityPattern().IsMatch(response))
        {
            violation = "A resposta atribui identidade humana ou vínculo real à equipe virtual.";
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

        if (!context.CanExposeTechnicalDetails &&
            (TechnicalVocabularyPattern().IsMatch(response) || InternalIdentifierPattern().IsMatch(response)))
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
            cota|quota|tokens?|
            worktrees?|branches?|sql|digest|utc|backlog|ready|cards?|risk\s*tier|
            gates?|projection\s*mismatch|stack\s*trace|logs?|c[oó]digo\s+t[eé]cnico|
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
        @"(?ix)\b(
            funcion[aá]ri[oa]\s+human[oa] |
            empregad[oa]\s+human[oa] |
            pessoa\s+real |
            v[ií]nculo\s+empregat[ií]cio |
            sou\s+(?:uma\s+)?(?:pessoa|humana)
        )\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex FalseHumanIdentityPattern();

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
