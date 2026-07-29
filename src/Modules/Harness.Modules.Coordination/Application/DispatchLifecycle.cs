namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Tipos completos do ciclo de vida de um despacho (B5).
///
/// O protocolo anterior tinha três tipos — heartbeat, checkpoint, completion — e isso obrigava a
/// espremer desfechos diferentes no mesmo canal. O caso que mais custou: <b>escalação chegava como
/// falha</b>. Um agente que para para perguntar não fracassou; tratá-lo como fracasso queima a
/// tentativa, aciona retentativa e transforma uma dúvida de trinta segundos em rodada perdida.
///
/// Igualmente separados aqui: <see cref="DecisionGate"/>, que é decisão de PLANO da Bruna, e a
/// pergunta do agente ao humano (<c>agent_requests</c>, contrato da F13 que esta fase não altera).
/// Confundir os dois faz a Bruna esperar por um humano que ninguém chamou.
/// </summary>
public static class DispatchLifecycleTypes
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

    /// <summary>Nome legado de <see cref="WorkerDone"/>; aceito para não quebrar runners em voo.</summary>
    public const string LegacyCompletion = "completion";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Status, Dispatch, Heartbeat, Checkpoint, WorkerDone,
        Escalation, DecisionGate, MergeReady, LegacyCompletion
    };

    /// <summary>
    /// Tipos que ENCERRAM o turno do agente. Depois de reportar um deles, o agente não fala mais
    /// sobre a tentativa — é isso que torna a conclusão contável exatamente uma vez.
    /// </summary>
    public static IReadOnlySet<string> Terminal { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkerDone, Escalation, LegacyCompletion
    };

    /// <summary>Escalação encerra o turno mas NÃO é falha: nenhuma retentativa é consumida.</summary>
    public static bool IsFailureBearing(string messageType) =>
        string.Equals(messageType, WorkerDone, StringComparison.Ordinal) ||
        string.Equals(messageType, LegacyCompletion, StringComparison.Ordinal);

    public static bool EndsTurn(string messageType) => Terminal.Contains(messageType);

    public static string Canonical(string messageType) =>
        string.Equals(messageType, LegacyCompletion, StringComparison.Ordinal) ? WorkerDone : messageType;
}

/// <summary>
/// Como o trabalho chegou. A distinção existe porque redescrever trabalho avulso como orquestrado
/// é a mentira mais barata que o sistema poderia contar — e a que mais corrompe as métricas, o
/// aprendizado e a confiança do dono no que a equipe diz ter feito.
/// </summary>
public enum WorkProvenance
{
    /// <summary>Nasceu de um despacho ativo da Bruna e foi verificado contra ele.</summary>
    Orchestrated = 0,

    /// <summary>Chegou sem despacho ativo correspondente. É registrado como tal, para sempre.</summary>
    Unorchestrated = 1
}

/// <summary>O despacho vigente de um card, contra o qual toda autoridade é verificada.</summary>
public sealed record ActiveDispatch(string TaskId, string AttemptId, long FencingToken);

/// <summary>
/// A identidade que a mensagem REIVINDICA. <see cref="RunnerId"/> aparece aqui porque é útil para
/// roteamento e diagnóstico — e é justamente por isso que ele nunca entra na decisão de autoridade.
/// </summary>
public sealed record ClaimedIdentity(
    string TaskId,
    string AttemptId,
    long FencingToken,
    string? RunnerId = null);

public enum DispatchAuthorityVerdict
{
    Accepted = 0,

    /// <summary>Não há despacho ativo para este card: trabalho avulso, nunca orquestrado.</summary>
    RejectedNoActiveDispatch = 1,

    /// <summary>Fala de outra tentativa ou de outro card. Rejeitada e auditada.</summary>
    RejectedForeignAttempt = 2,

    /// <summary>Fencing antigo: é um resultado tardio de uma tentativa já superada.</summary>
    RejectedStaleFencing = 3,

    /// <summary>Fencing à frente do despacho ativo: ninguém legítimo sabe mais que o despacho.</summary>
    RejectedUnknownFencing = 4
}

public sealed record DispatchAuthorityDecision(
    DispatchAuthorityVerdict Verdict,
    string ReasonCode,
    WorkProvenance Provenance,
    bool RequiresAudit)
{
    public bool IsAccepted => Verdict == DispatchAuthorityVerdict.Accepted;
}
