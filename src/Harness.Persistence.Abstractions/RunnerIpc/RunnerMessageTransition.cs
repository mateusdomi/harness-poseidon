using Harness.SharedKernel.RunnerIpc;

namespace Harness.Persistence.Abstractions.RunnerIpc;

public static class RunnerMessageTransition
{
    public static RunnerMessageStoreResult? RejectInvalid(
        RunnerAttemptState? current,
        RunnerMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var expectedSequence = (current?.LastSequence ?? 0) + 1;
        if (message.Sequence != expectedSequence)
        {
            var rejection = message.Sequence < expectedSequence
                ? RunnerMessageRejection.StaleSequence
                : RunnerMessageRejection.SequenceGap;
            return RunnerMessageStoreResult.Rejected(rejection, expectedSequence);
        }

        if (current?.Completed == true)
        {
            return RunnerMessageStoreResult.Rejected(RunnerMessageRejection.AttemptAlreadyCompleted);
        }

        // AUTORIDADE POR IDENTIDADE, NÃO POR PROCESSO (B5).
        //
        // Aqui ficava a regra que mais destruía trabalho real: rejeitar quando o `runner_id` da
        // mensagem diferia do registrado. Um agente que reiniciava ganhava identificador novo e
        // tinha a própria conclusão recusada por "dono diferente" — trabalho feito, relatório
        // pronto, e o sistema jogando fora porque o processo que entregou não era o que começou.
        //
        // O que separa o agente reiniciado de um terceiro não é o identificador do processo: é o
        // fencing token, que só o despacho emite. Fencing igual ao registrado é a MESMA tentativa,
        // venha do processo que vier. Fencing antigo é resultado tardio de tentativa superada, e
        // publicá-lo sobrescreveria o trabalho da tentativa vigente.
        if (current is not null &&
            message.FencingToken > 0 &&
            current.FencingToken > 0 &&
            message.FencingToken < current.FencingToken)
        {
            return RunnerMessageStoreResult.Rejected(RunnerMessageRejection.StaleFencingToken);
        }

        return null;
    }
}
