using Harness.SharedKernel.RunnerIpc;

namespace Harness.Persistence.Abstractions.RunnerIpc;

public enum RunnerMessageRejection
{
    None,
    IdempotencyKeyConflict,
    StaleSequence,
    SequenceGap,
    AttemptAlreadyCompleted,

    /// <summary>
    /// Obsoleto: posse por identificador de processo. Mantido no enum para não renumerar valores
    /// já persistidos/serializados, mas nenhuma transição o produz — quem decide é o fencing.
    /// </summary>
    RunnerOwnerConflict,

    /// <summary>Resultado tardio de uma tentativa já superada pelo despacho vigente.</summary>
    StaleFencingToken,
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
