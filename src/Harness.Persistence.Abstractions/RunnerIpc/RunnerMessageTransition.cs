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

        if (current is not null && !string.Equals(current.RunnerId, message.RunnerId, StringComparison.Ordinal))
        {
            return RunnerMessageStoreResult.Rejected(RunnerMessageRejection.RunnerOwnerConflict);
        }

        return null;
    }
}
