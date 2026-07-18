using Harness.Persistence.Abstractions.Messaging;

namespace Harness.Host.Workers;

public sealed record OutboxDispatcherOptions(
    string Owner,
    TimeSpan LeaseDuration,
    TimeSpan PollInterval,
    int MaximumBatchSize,
    OutboxRetryPolicy RetryPolicy)
{
    public void Validate()
    {
        OutboxContractValidator.ValidateAcquire(
            Owner,
            LeaseDuration,
            DateTimeOffset.UnixEpoch);
        if (PollInterval < TimeSpan.FromMilliseconds(10) ||
            PollInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval),
                "Poll interval must be between 10 milliseconds and one minute.");
        }

        if (MaximumBatchSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumBatchSize),
                "Batch size must be between 1 and 1000.");
        }

        ArgumentNullException.ThrowIfNull(RetryPolicy);
        RetryPolicy.Validate();
    }
}
