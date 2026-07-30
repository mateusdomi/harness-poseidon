namespace Harness.SharedKernel.RunnerIpc;

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
