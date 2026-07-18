namespace Harness.Persistence.Abstractions.DurableExecution;

public sealed record DurableRetryPolicy
{
    public DurableRetryPolicy(
        int maxAttempts,
        TimeSpan initialDelay,
        decimal multiplier,
        TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelay, TimeSpan.Zero);
        if (multiplier < 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Retry multiplier cannot be less than one.");
        }

        if (maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                "Maximum retry delay cannot be less than the initial delay.");
        }

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay;
        Multiplier = multiplier;
        MaximumDelay = maximumDelay;
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public decimal Multiplier { get; }

    public TimeSpan MaximumDelay { get; }

    public TimeSpan DelayAfterFailure(int failedAttemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failedAttemptNumber);
        if (InitialDelay == TimeSpan.Zero || MaximumDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var maximumTicks = (decimal)MaximumDelay.Ticks;
        var delayTicks = (decimal)InitialDelay.Ticks;
        for (var attempt = 1; attempt < failedAttemptNumber; attempt++)
        {
            if (delayTicks >= maximumTicks / Multiplier)
            {
                return MaximumDelay;
            }

            delayTicks *= Multiplier;
        }

        return TimeSpan.FromTicks((long)decimal.Floor(decimal.Min(delayTicks, maximumTicks)));
    }
}
