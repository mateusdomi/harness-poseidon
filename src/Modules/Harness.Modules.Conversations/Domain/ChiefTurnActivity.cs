namespace Harness.Modules.Conversations.Domain;

/// <summary>
/// Estados granulares e observáveis do ciclo de vida de um turno do Chefe
/// (C3/ADR-019 estendido). Cada estado descreve, de forma HONESTA, uma fase real
/// pela qual o turno passa — nunca uma resposta fabricada. Publicados como
/// <c>chief.turnStateChanged</c> em tempo real para o balão da conversa.
///
/// A vocabulário granular convive com os estados grossos já emitidos pelo store
/// (<c>pending</c>/<c>processing</c>): <see cref="Received"/> é o nome canônico do
/// turno recém-registrado e enfileirado; <see cref="ReadingContext"/>,
/// <see cref="Thinking"/>, <see cref="Planning"/> e <see cref="Delegating"/> são as
/// fases pelas quais o worker realmente passa ao processar o turno.
/// </summary>
public enum ChiefTurnActivity
{
    /// <summary>Mensagem registrada e enfileirada (equivale ao <c>pending</c> grosso).</summary>
    Received,

    /// <summary>Worker montando o pacote de contexto (digest + bundle governado).</summary>
    ReadingContext,

    /// <summary>Chamada ao modelo em andamento — o Chefe está "pensando".</summary>
    Thinking,

    /// <summary>Resposta recebida; estruturando saída e avaliando (crítico independente).</summary>
    Planning,

    /// <summary>Materializando demandas: o Chefe está delegando trabalho a agentes.</summary>
    Delegating,

    /// <summary>Um agente delegado está executando (com nome + início do trabalho).</summary>
    AgentWorking,

    /// <summary>Trabalho concluído aguardando revisão/aprovação humana.</summary>
    AwaitingReview,

    /// <summary>Recusado por prontidão: nenhuma resposta é fabricada (terminal).</summary>
    Blocked,

    /// <summary>Resposta entregue e turno encerrado com sucesso (terminal).</summary>
    Completed,

    /// <summary>Turno falhou após esgotar as tentativas ou por saída inválida (terminal).</summary>
    Failed,
}

/// <summary>
/// Máquina de estados pura do turno do Chefe. Define o nome de fio (snake_case)
/// de cada estado e valida transições — sem dependência de I/O, para teste unitário
/// direto das transições. É a fonte da verdade do vocabulário granular compartilhado
/// com o frontend (contrato <c>chief.turnStateChanged</c>).
/// </summary>
public static class ChiefTurnActivityState
{
    private static readonly Dictionary<ChiefTurnActivity, string> WireNames =
        new()
        {
            [ChiefTurnActivity.Received] = "received",
            [ChiefTurnActivity.ReadingContext] = "reading_context",
            [ChiefTurnActivity.Thinking] = "thinking",
            [ChiefTurnActivity.Planning] = "planning",
            [ChiefTurnActivity.Delegating] = "delegating",
            [ChiefTurnActivity.AgentWorking] = "agent_working",
            [ChiefTurnActivity.AwaitingReview] = "awaiting_review",
            [ChiefTurnActivity.Blocked] = "blocked",
            [ChiefTurnActivity.Completed] = "completed",
            [ChiefTurnActivity.Failed] = "failed",
        };

    // Grafo de transições honestas. Estados terminais não têm saída.
    private static readonly Dictionary<ChiefTurnActivity, HashSet<ChiefTurnActivity>> Allowed =
        new()
        {
            [ChiefTurnActivity.Received] = Set(
                ChiefTurnActivity.ReadingContext, ChiefTurnActivity.Thinking,
                ChiefTurnActivity.Blocked, ChiefTurnActivity.Failed),
            [ChiefTurnActivity.ReadingContext] = Set(
                ChiefTurnActivity.Thinking, ChiefTurnActivity.Failed),
            [ChiefTurnActivity.Thinking] = Set(
                ChiefTurnActivity.Planning, ChiefTurnActivity.Delegating,
                ChiefTurnActivity.Completed, ChiefTurnActivity.Failed),
            [ChiefTurnActivity.Planning] = Set(
                ChiefTurnActivity.Delegating, ChiefTurnActivity.Completed, ChiefTurnActivity.Failed),
            [ChiefTurnActivity.Delegating] = Set(
                ChiefTurnActivity.AgentWorking, ChiefTurnActivity.Completed, ChiefTurnActivity.Failed),
            [ChiefTurnActivity.AgentWorking] = Set(
                ChiefTurnActivity.AwaitingReview, ChiefTurnActivity.AgentWorking,
                ChiefTurnActivity.Completed, ChiefTurnActivity.Failed),
            [ChiefTurnActivity.AwaitingReview] = Set(
                ChiefTurnActivity.AgentWorking, ChiefTurnActivity.Completed, ChiefTurnActivity.Failed),
            [ChiefTurnActivity.Blocked] = Set(),
            [ChiefTurnActivity.Completed] = Set(),
            [ChiefTurnActivity.Failed] = Set(),
        };

    /// <summary>Nome de fio snake_case usado no payload do evento realtime.</summary>
    public static string Wire(ChiefTurnActivity activity) => WireNames[activity];

    /// <summary>É um estado terminal (sem transição de saída)?</summary>
    public static bool IsTerminal(ChiefTurnActivity activity) => Allowed[activity].Count == 0;

    /// <summary>A transição <paramref name="from"/> → <paramref name="to"/> é honesta/permitida?</summary>
    public static bool CanTransition(ChiefTurnActivity from, ChiefTurnActivity to) =>
        Allowed[from].Contains(to);

    /// <summary>Todos os estados, na ordem canônica do ciclo de vida.</summary>
    public static IReadOnlyList<ChiefTurnActivity> All { get; } =
        Enum.GetValues<ChiefTurnActivity>();

    private static HashSet<ChiefTurnActivity> Set(params ChiefTurnActivity[] values) =>
        new(values);
}
