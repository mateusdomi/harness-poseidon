using Harness.SharedKernel.RunnerIpc;

namespace Harness.Persistence.Abstractions.RunnerIpc;

public interface IRunnerMessageStore
{
    Task<RunnerMessageStoreResult> ApplyAsync(
        RunnerMessageEnvelope message,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<RunnerAttemptState?> ReadAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken = default);
}
