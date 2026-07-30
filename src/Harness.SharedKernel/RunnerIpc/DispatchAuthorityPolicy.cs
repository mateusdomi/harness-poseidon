namespace Harness.SharedKernel.RunnerIpc;

/// <summary>
/// Autoridade de uma mensagem sobre uma tentativa (B5).
///
/// A regra que esta política inverte: <b>quem fala pelo trabalho é o par
/// (card, tentativa) validado pelo fencing — nunca o identificador do processo</b>. O protocolo
/// anterior decidia posse por <c>runner_id</c>, e isso produzia exatamente o pior desfecho
/// possível: um agente que reiniciava recebia um identificador novo e tinha a própria conclusão
/// recusada por "dono diferente". O trabalho estava feito, o relatório existia, e o sistema o
/// jogava fora porque o processo que o entregou não era o mesmo que começou.
///
/// Identificador de processo/sessão serve para ROTEAR e diagnosticar. O que decide proveniência é
/// o fencing token, porque é ele que o despacho emite e é ele que distingue "a mesma tentativa,
/// outro processo" de "outra tentativa, resultado tardio".
///
/// Simetricamente, o que esta política NÃO afrouxa: mensagem de tentativa alheia, de card alheio
/// ou com fencing vencido é rejeitada E auditada. Aceitar por engano é pior que recusar, porque
/// atribui a um card trabalho que não é dele.
/// </summary>
public static class DispatchAuthorityPolicy
{
    public const string ReasonAccepted = "authority.accepted";
    public const string ReasonAcceptedAfterRestart = "authority.accepted_after_restart";
    public const string ReasonNoActiveDispatch = "authority.no_active_dispatch";
    public const string ReasonForeignAttempt = "authority.foreign_attempt";
    public const string ReasonStaleFencing = "authority.stale_fencing";
    public const string ReasonUnknownFencing = "authority.unknown_fencing";

    /// <summary>
    /// Decide se a mensagem tem autoridade sobre a tentativa. <paramref name="active"/> nulo
    /// significa que não há despacho vigente para o card.
    /// </summary>
    public static DispatchAuthorityDecision Decide(
        ClaimedIdentity claimed,
        ActiveDispatch? active)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimed.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimed.AttemptId);

        // Sem despacho ativo não há o que orquestrar: o trabalho é avulso e será registrado como
        // avulso. Ele não é apagado — é apenas honesto sobre a própria origem.
        if (active is null)
        {
            return new DispatchAuthorityDecision(
                DispatchAuthorityVerdict.RejectedNoActiveDispatch,
                ReasonNoActiveDispatch,
                WorkProvenance.Unorchestrated,
                RequiresAudit: true);
        }

        if (!string.Equals(claimed.TaskId, active.TaskId, StringComparison.Ordinal) ||
            !string.Equals(claimed.AttemptId, active.AttemptId, StringComparison.Ordinal))
        {
            return new DispatchAuthorityDecision(
                DispatchAuthorityVerdict.RejectedForeignAttempt,
                ReasonForeignAttempt,
                WorkProvenance.Unorchestrated,
                RequiresAudit: true);
        }

        if (claimed.FencingToken < active.FencingToken)
        {
            // Resultado tardio de uma tentativa já superada. Publicá-lo sobrescreveria o trabalho
            // da tentativa vigente com o de uma que o sistema já deu por encerrada.
            return new DispatchAuthorityDecision(
                DispatchAuthorityVerdict.RejectedStaleFencing,
                ReasonStaleFencing,
                WorkProvenance.Unorchestrated,
                RequiresAudit: true);
        }

        if (claimed.FencingToken > active.FencingToken)
        {
            // Ninguém legítimo tem fencing à frente do despacho: ou é forjado, ou o remetente está
            // falando de um despacho que este processo não conhece. Nos dois casos, não decide nada.
            return new DispatchAuthorityDecision(
                DispatchAuthorityVerdict.RejectedUnknownFencing,
                ReasonUnknownFencing,
                WorkProvenance.Unorchestrated,
                RequiresAudit: true);
        }

        return new DispatchAuthorityDecision(
            DispatchAuthorityVerdict.Accepted,
            ReasonAccepted,
            WorkProvenance.Orchestrated,
            RequiresAudit: false);
    }

    /// <summary>
    /// Mesma decisão, informando se o processo mudou desde o despacho. O identificador diferente
    /// NÃO altera o veredito — ele só qualifica o motivo, para a auditoria conseguir distinguir
    /// "reiniciou e voltou" de "nunca saiu".
    /// </summary>
    public static DispatchAuthorityDecision DecideWithRestartAwareness(
        ClaimedIdentity claimed,
        ActiveDispatch? active,
        string? dispatchedRunnerId)
    {
        var decision = Decide(claimed, active);
        if (!decision.IsAccepted)
        {
            return decision;
        }

        var restarted =
            !string.IsNullOrWhiteSpace(dispatchedRunnerId) &&
            !string.IsNullOrWhiteSpace(claimed.RunnerId) &&
            !string.Equals(claimed.RunnerId, dispatchedRunnerId, StringComparison.Ordinal);

        return restarted
            ? decision with { ReasonCode = ReasonAcceptedAfterRestart }
            : decision;
    }
}

