using Harness.SharedKernel.RunnerIpc;

namespace Harness.Persistence.Abstractions.RunnerIpc;

public enum RunnerMessageRejection
{
    None,
    IdempotencyKeyConflict,
    StaleSequence,
    SequenceGap,
    AttemptAlreadyCompleted,
    RunnerOwnerConflict,
}

public sealed record RunnerMessageStoreResult(
    RunnerMessageReceipt? Receipt,
    RunnerMessageRejection Rejection,
    long? ExpectedSequence = null)
{
    public static RunnerMessageStoreResult Succeeded(RunnerMessageReceipt receipt) =>
        new(receipt, RunnerMessageRejection.None);

    public static RunnerMessageStoreResult Rejected(
        RunnerMessageRejection rejection,
        long? expectedSequence = null) =>
        new(null, rejection, expectedSequence);
}
