namespace Harness.SharedKernel.RunnerIpc;

/// <summary>
/// Tipos do protocolo IPC do runner — a fonte única (B5/F13).
///
/// O protocolo nasceu com três tipos (heartbeat, checkpoint, completion) e isso obrigava a
/// espremer desfechos diferentes no mesmo canal. O caso que mais custou: <b>escalação chegava
/// como falha</b>. Um agente que para para perguntar não fracassou; tratá-lo como fracasso queima
/// a tentativa, aciona retentativa e transforma uma dúvida de trinta segundos em rodada perdida.
///
/// Os tipos moram aqui, no núcleo, e não no módulo de coordenação, porque quem depende deles é o
/// contrato IPC — processador do Host, stores e runner. <c>DispatchLifecycleTypes</c> delega para
/// cá: duas listas concorrentes de tipos é exatamente o defeito que esta fase elimina.
/// </summary>
public static class RunnerMessageTypes
{
    /// <summary>Progresso informativo: não encerra turno, não prova conclusão.</summary>
    public const string Status = "status";

    /// <summary>Chefe → agente: a ordem de trabalho da tentativa.</summary>
    public const string Dispatch = "dispatch";

    /// <summary>Sinal de vida. Prova vivo, nunca concluído (política de espera da F12).</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>Estado retomável do card. Não encerra a espera.</summary>
    public const string Checkpoint = "checkpoint";

    /// <summary>Desfecho do agente — sucesso OU falha. Encerra o turno, exatamente uma vez.</summary>
    public const string WorkerDone = "worker_done";

    /// <summary>Agente parou para perguntar. NÃO é falha e não consome retentativa.</summary>
    public const string Escalation = "escalation";

    /// <summary>Decisão de plano da Bruna. Distinta da pergunta do agente ao humano.</summary>
    public const string DecisionGate = "decision_gate";

    /// <summary>O trabalho está pronto para integração serializada.</summary>
    public const string MergeReady = "merge_ready";

    /// <summary>
    /// Nome legado de <see cref="WorkerDone"/>. Continua aceito porque um runner em voo durante a
    /// atualização não pode ter a própria conclusão recusada por causa do nome do campo — seria
    /// trabalho real descartado por formalidade.
    /// </summary>
    public const string Completion = "completion";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Status,
        Dispatch,
        Heartbeat,
        Checkpoint,
        WorkerDone,
        Escalation,
        DecisionGate,
        MergeReady,
        Completion,
    };

    /// <summary>
    /// Tipos que ENCERRAM o turno do agente. Depois de reportar um deles, o agente não fala mais
    /// sobre a tentativa — é isso que torna a conclusão contável exatamente uma vez.
    /// </summary>
    public static IReadOnlySet<string> Terminal { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkerDone,
        Escalation,
        Completion,
    };

    /// <summary>
    /// Encerrou o turno E carrega desfecho de trabalho. Escalação encerra sem ser falha: nenhuma
    /// retentativa é consumida por uma pergunta.
    /// </summary>
    public static bool IsFailureBearing(string messageType) =>
        string.Equals(messageType, WorkerDone, StringComparison.Ordinal) ||
        string.Equals(messageType, Completion, StringComparison.Ordinal);

    public static bool EndsTurn(string messageType) => Terminal.Contains(messageType);

    /// <summary>Normaliza o nome legado para o canônico, para o resto do sistema ver um termo só.</summary>
    public static string Canonical(string messageType) =>
        string.Equals(messageType, Completion, StringComparison.Ordinal) ? WorkerDone : messageType;
}
