namespace Harness.Modules.Conversations.Application;

/// <summary>
/// Frase humana de um motivo, com o próximo passo quando existe um. <see cref="NextStep"/> é nulo
/// quando não há nada que o dono possa fazer — e nesse caso inventar uma ação seria pior que calar.
/// </summary>
public sealed record HumanReason(string Phrase, string? NextStep = null)
{
    /// <summary>Texto completo para o chat: o que aconteceu e, se houver, o que fazer.</summary>
    public string Compose() =>
        string.IsNullOrWhiteSpace(NextStep) ? Phrase : $"{Phrase} {NextStep}";
}

/// <summary>
/// Tradução de código de motivo para linguagem de negócio (D15).
///
/// O exemplo homologado que motivou esta classe mostrava a Bruna dizendo
/// <c>account.role_not_allowed</c> ao dono. Um código cru não é apenas feio: ele é uma
/// não-resposta. O dono não sabe o que aconteceu, não sabe se a culpa é dele, e não sabe o que
/// fazer em seguida — então volta a perguntar, e a conversa gasta dois turnos para não dizer nada.
///
/// Duas regras sustentam esta tradução:
///
/// 1. <b>Código cru NUNCA sai daqui</b>, nem o desconhecido. Um mapa incompleto é normal — o
///    sistema ganha códigos novos toda semana. O que não pode acontecer é o código vazar porque
///    ninguém o traduziu ainda; por isso o caminho de fallback devolve frase de negócio, e o
///    código segue apenas para a auditoria, onde ele é útil.
/// 2. <b>Próximo passo quando existe</b>. "A conta está sem cota" deixa o dono paralisado;
///    "a Larissa atingiu o limite de trabalho de hoje — posso passar a tarefa para outra pessoa da
///    equipe?" o deixa no comando.
///
/// A humanização não inventa: ela não promete prazo, não afirma conclusão e não nega ser sistema.
/// Onde o fato é "não sei", a frase diz que a equipe está verificando.
/// </summary>
public static class ReasonCodeHumanizer
{
    /// <summary>
    /// Frase usada quando o código não está no mapa. Ela é vaga de propósito: melhor admitir que a
    /// equipe está verificando do que arriscar uma explicação errada — ou vazar o código.
    /// </summary>
    public static readonly HumanReason Unknown = new(
        "Apareceu um imprevisto que a equipe está verificando agora.",
        "Assim que eu souber o que é, eu te conto por aqui.");

    private static readonly Dictionary<string, HumanReason> Map = new(StringComparer.Ordinal)
    {
        // --- Pessoas da equipe: competência, jornada e admissão ---
        ["account.role_not_allowed"] = new(
            "A pessoa que eu escolhi ainda não tem a competência necessária para esta tarefa.",
            "Posso preparar essa habilitação ou passar a tarefa para outra pessoa da equipe — como prefere?"),
        ["account.role_unknown"] = new(
            "Pedi uma competência que ainda não existe no quadro da equipe.",
            "Posso cadastrá-la ou escolher a pessoa mais próxima do que a tarefa pede."),
        ["account.quota_limited"] = new(
            "Essa pessoa da equipe atingiu o limite de trabalho da jornada dela.",
            "Posso passar a tarefa para outra pessoa agora, ou aguardar a jornada renovar."),
        ["account.cooling_down"] = new(
            "Essa pessoa precisa de um intervalo antes de assumir a próxima tarefa.",
            "Já reagendei; se preferir, passo para outra pessoa da equipe."),
        ["account.authentication_required"] = new(
            "Essa pessoa está em admissão e ainda precisa que você conclua a contratação dela.",
            "São dois minutos em Equipe; posso te guiar no passo a passo."),
        ["account.disabled"] = new(
            "Essa pessoa está fora do quadro no momento.",
            "Posso reativá-la ou seguir com outra pessoa da equipe."),
        ["account.not_available"] = new(
            "A pessoa que eu queria para esta tarefa está indisposta agora.",
            "Deve voltar em breve; enquanto isso posso redistribuir a tarefa."),
        ["account.not_found"] = new(
            "Não encontrei essa pessoa no quadro da equipe.",
            "Posso escolher outra pessoa com a mesma competência."),
        ["account.concurrency_exhausted"] = new(
            "A equipe está com todas as pessoas ocupadas neste momento.",
            "A tarefa entra na fila e começa assim que alguém liberar."),
        ["account.actor_cannot_be_critic"] = new(
            "Quem fez o trabalho não pode ser quem confere — a conferência é sempre de um colega.",
            "Já escolhi outra pessoa para revisar."),

        // --- Configuração inicial da conversa ---
        ["provider_account.missing"] = new(
            "Ainda falta concluir a contratação de uma pessoa da equipe.",
            "Posso te levar à configuração inicial para resolver isso agora."),
        ["model.none_chat_enabled"] = new(
            "A equipe ainda não tem um modo de trabalho habilitado para conversar.",
            "Escolha um modo na configuração inicial e eu continuo daqui."),
        ["workflow.unbound"] = new(
            "Este projeto ainda não tem as etapas de trabalho definidas.",
            "Vincule um fluxo de trabalho e eu organizo a entrega a partir dele."),
        ["chief.model_unresolved"] = new(
            "Meu modo de trabalho ainda não está definido para este projeto.",
            "Conclua essa escolha na configuração inicial e eu retomo sua mensagem."),
        ["execution.not_ready"] = new(
            "A equipe ainda está concluindo a preparação necessária para começar.",
            "Sua mensagem ficou salva; assim que a preparação terminar, seguimos por aqui."),

        // --- Prontidão da tarefa ---
        ["dor.card_type.not_dispatchable"] = new(
            "Esse item precisa de uma decisão sua antes de virar trabalho da equipe.",
            "Quando você me disser como quer seguir, eu coloco a equipe nele."),
        ["dor.instruction.missing"] = new(
            "Essa tarefa ainda está sem o enunciado do que precisa ser feito.",
            "Posso escrever uma proposta e te mostrar antes de começar."),
        ["dor.blocked"] = new(
            "Essa tarefa está travada por uma pendência anterior.",
            "Vou te mostrar o que falta para ela destravar."),

        // --- Circuito da tarefa e espera (B4) ---
        ["card.circuit_open"] = new(
            "Essa tarefa falhou três vezes seguidas, então parei de insistir.",
            "O problema está no enunciado dela, não em quem executou — vou reescrever e te mostrar."),
        ["wait.escalated"] = new(
            "A pessoa que está nessa tarefa parou para tirar uma dúvida.",
            "Ela precisa de uma decisão sua para continuar."),
        ["wait.deadline_checkpoint"] = new(
            "Essa tarefa está levando mais tempo do que eu estimei.",
            "O trabalho está salvo e continua — a estimativa é que estava curta."),

        // --- Guardas de laço (B9) ---
        ["chief_loop.causal_cycle"] = new(
            "Percebi que eu estava girando em círculo nesta demanda e me interrompi.",
            "Vou replanejar de outro jeito e te contar o que mudou."),
        ["chief_loop.demand_ceiling"] = new(
            "Já fiz muitas rodadas de trabalho nesta demanda sem fechá-la.",
            "Prefiro combinar o próximo passo com você antes de continuar."),
        ["chief_loop.rate_window"] = new(
            "Estou trabalhando rápido demais nesta demanda e vou dar uma pausa curta.",
            "Retomo em instantes, sem perder nada do que já foi feito."),

        // --- Autoridade e conclusão (B5) ---
        ["authority.stale_fencing"] = new(
            "Chegou um resultado atrasado de uma tentativa que já havia sido substituída.",
            "Descartei para não sobrescrever o trabalho mais recente."),
        ["authority.foreign_attempt"] = new(
            "Chegou um resultado que não corresponde a nenhuma tarefa em andamento.",
            "Registrei e ignorei, para não atribuir a você um trabalho que não é seu."),
        ["completion.already_completed"] = new(
            "Essa tarefa já havia sido encerrada antes.",
            null),
    };

    /// <summary>Todos os códigos com tradução explícita.</summary>
    public static IReadOnlyCollection<string> KnownCodes => Map.Keys;

    /// <summary>
    /// Traduz o código. NUNCA devolve o código: desconhecido cai em <see cref="Unknown"/>, porque
    /// vazar o código é o defeito que esta classe existe para impedir.
    /// </summary>
    public static HumanReason Humanize(string? reasonCode) =>
        !string.IsNullOrWhiteSpace(reasonCode) && Map.TryGetValue(reasonCode.Trim(), out var reason)
            ? reason
            : Unknown;

    /// <summary>Verdadeiro quando existe tradução explícita — usado pelo gate de cobertura.</summary>
    public static bool HasExplicitTranslation(string? reasonCode) =>
        !string.IsNullOrWhiteSpace(reasonCode) && Map.ContainsKey(reasonCode.Trim());
}
