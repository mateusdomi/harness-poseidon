namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Fatos observáveis de uma espera por agente já despachado — puros, sem IO.
/// <see cref="WaitDeadline"/> é o prazo da espera (o antigo "timeout"); ele NÃO decide nada sobre a
/// vida do agente. <see cref="LastHeartbeatAt"/> prova que o agente está VIVO, e não que terminou.
/// <see cref="ProcessAlive"/> é o fato do sistema operacional, não uma inferência de tempo.
/// </summary>
public sealed record AgentWaitFacts(
    DateTimeOffset Now,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset? WaitDeadline = null,
    DateTimeOffset? LastHeartbeatAt = null,
    bool ProcessAlive = true,
    bool CompletionReported = false,
    bool EscalationReported = false,
    DateTimeOffset? LastCheckpointAt = null);

/// <summary>Ação decidida para uma espera. Só quatro delas encerram a espera.</summary>
public enum AgentWaitAction
{
    /// <summary>Continuar esperando: nada observável mudou o estado da espera.</summary>
    KeepWaiting = 0,

    /// <summary>Prazo vencido: tira um checkpoint e CONTINUA esperando. Prazo não mata ninguém.</summary>
    Checkpoint = 1,

    /// <summary>O agente reportou conclusão.</summary>
    ConcludeCompleted = 2,

    /// <summary>O agente escalou — pergunta ao humano não é falha, mas encerra a espera.</summary>
    ConcludeEscalated = 3,

    /// <summary>O processo morreu: o fato do SO, não uma suposição por lentidão.</summary>
    ReclaimProcessDead = 4,

    /// <summary>Lease expirada E sem heartbeat recente: ninguém está do outro lado.</summary>
    ReclaimLeaseExpiredWithoutHeartbeat = 5
}

/// <summary>
/// Veredito de uma espera. <see cref="EndsWait"/> distingue o que encerra a espera do que apenas
/// registra progresso; <see cref="MayReclaim"/> é a ÚNICA autorização para matar ou reenfileirar a
/// tentativa — e ela jamais é concedida enquanto o agente estiver vivo.
/// </summary>
public sealed record AgentWaitVerdict(
    AgentWaitAction Action,
    string ReasonCode,
    bool EndsWait,
    bool MayReclaim);

/// <summary>
/// Política PURA e determinística da espera por um agente despachado (B4).
///
/// A regra que ela encoda, e que o código anterior violava: <b>timeout é checkpoint, não morte</b>.
/// Um prazo vencido significa apenas que a espera durou mais do que se estimou — estimativa errada
/// é do estimador, não do agente. Um agente que trabalha há mais tempo que o previsto continua
/// produzindo; matá-lo destrói trabalho real e ainda recomeça do zero o que já estava adiantado.
///
/// Igualmente encodada: <b>heartbeat prova vivo, não concluído</b>. Heartbeat recente autoriza
/// esperar mais; ele nunca autoriza dar a tentativa por terminada, e a sua ausência isolada também
/// não autoriza matar — só a soma "lease expirada + sem heartbeat" descreve alguém que não está
/// mais do outro lado.
///
/// Por isso apenas quatro fatos encerram a espera: conclusão reportada, escalação, morte do
/// processo, ou lease expirada sem heartbeat. Lentidão não é nenhum deles.
/// </summary>
public static class AgentWaitPolicy
{
    /// <summary>Janela padrão em que um heartbeat ainda é considerado recente.</summary>
    public static readonly TimeSpan DefaultHeartbeatGrace = TimeSpan.FromMinutes(2);

    public const string ReasonCompletionReported = "wait.completed";
    public const string ReasonEscalationReported = "wait.escalated";
    public const string ReasonProcessDead = "wait.process_dead";
    public const string ReasonLeaseExpiredWithoutHeartbeat = "wait.lease_expired_without_heartbeat";
    public const string ReasonDeadlineCheckpoint = "wait.deadline_checkpoint";
    public const string ReasonAgentAlive = "wait.agent_alive";
    public const string ReasonWithinDeadline = "wait.within_deadline";

    public static AgentWaitVerdict Decide(AgentWaitFacts facts, TimeSpan? heartbeatGrace = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var grace = heartbeatGrace ?? DefaultHeartbeatGrace;
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatGrace),
                "A janela de heartbeat não pode ser negativa.");
        }

        // 1. Conclusão e escalação são as saídas legítimas — elas vêm do agente, não do relógio.
        if (facts.CompletionReported)
        {
            return new AgentWaitVerdict(
                AgentWaitAction.ConcludeCompleted, ReasonCompletionReported, EndsWait: true, MayReclaim: false);
        }

        if (facts.EscalationReported)
        {
            return new AgentWaitVerdict(
                AgentWaitAction.ConcludeEscalated, ReasonEscalationReported, EndsWait: true, MayReclaim: false);
        }

        // 2. Morte do processo é fato observado do SO. Só aqui, e no item 3, reclamar é permitido.
        if (!facts.ProcessAlive)
        {
            return new AgentWaitVerdict(
                AgentWaitAction.ReclaimProcessDead, ReasonProcessDead, EndsWait: true, MayReclaim: true);
        }

        // 3. Lease expirada NÃO basta: com heartbeat recente há alguém vivo do outro lado, e matar
        //    um agente vivo é exatamente o defeito que esta política existe para impedir.
        var heartbeatIsRecent = IsHeartbeatRecent(facts, grace);
        if (facts.Now >= facts.LeaseExpiresAt && !heartbeatIsRecent)
        {
            return new AgentWaitVerdict(
                AgentWaitAction.ReclaimLeaseExpiredWithoutHeartbeat,
                ReasonLeaseExpiredWithoutHeartbeat,
                EndsWait: true,
                MayReclaim: true);
        }

        // 4. Prazo vencido vira checkpoint — uma vez por prazo, para não reescrever o mesmo estado
        //    a cada ciclo do watchdog — e a espera SEGUE.
        if (facts.WaitDeadline is { } deadline &&
            facts.Now >= deadline &&
            (facts.LastCheckpointAt is null || facts.LastCheckpointAt < deadline))
        {
            return new AgentWaitVerdict(
                AgentWaitAction.Checkpoint, ReasonDeadlineCheckpoint, EndsWait: false, MayReclaim: false);
        }

        return new AgentWaitVerdict(
            AgentWaitAction.KeepWaiting,
            heartbeatIsRecent ? ReasonAgentAlive : ReasonWithinDeadline,
            EndsWait: false,
            MayReclaim: false);
    }

    /// <summary>
    /// Um agente é considerado VIVO quando o processo existe e o heartbeat é recente. Nenhuma
    /// espera pode matar ou reenfileirar um agente vivo, por mais que o prazo tenha estourado.
    /// </summary>
    public static bool IsAgentAlive(AgentWaitFacts facts, TimeSpan? heartbeatGrace = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.ProcessAlive && IsHeartbeatRecent(facts, heartbeatGrace ?? DefaultHeartbeatGrace);
    }

    // Heartbeat adiantado (relógios levemente dessincronizados) conta como recente: na dúvida entre
    // "vivo" e "morto", esta política escolhe vivo — o erro barato é esperar mais.
    private static bool IsHeartbeatRecent(AgentWaitFacts facts, TimeSpan grace) =>
        facts.LastHeartbeatAt is { } heartbeat && facts.Now - heartbeat <= grace;
}
