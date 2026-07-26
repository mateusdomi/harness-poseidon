using System.Diagnostics;

namespace Harness.Modules.Coordination.Application;

public sealed record MergeContentionSnapshot(
    long Enqueued,
    long Serialized,
    long Contended,
    int Waiting,
    int Active,
    TimeSpan TotalWait,
    TimeSpan MaximumWait)
{
    public double ContentionRatio => Enqueued == 0 ? 0d : (double)Contended / Enqueued;

    public bool ShouldEvaluateValkey(
        bool serverMode,
        int minimumSamples = 20,
        double contentionThreshold = 0.25d)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSamples);

        if (contentionThreshold is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(contentionThreshold));
        }

        return serverMode &&
            Enqueued >= minimumSamples &&
            ContentionRatio >= contentionThreshold;
    }
}

public interface ISerializedMergeCoordinator
{
    Task<T> ExecuteAsync<T>(
        string cardId,
        Func<CancellationToken, Task<T>> merge,
        CancellationToken cancellationToken = default);

    MergeContentionSnapshot Snapshot();
}

public sealed class SerializedMergeCoordinator : ISerializedMergeCoordinator, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _enqueued;
    private long _serialized;
    private long _contended;
    private long _totalWaitTicks;
    private long _maximumWaitTicks;
    private int _waiting;
    private int _active;

    public async Task<T> ExecuteAsync<T>(
        string cardId,
        Func<CancellationToken, Task<T>> merge,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentNullException.ThrowIfNull(merge);

        Interlocked.Increment(ref _enqueued);
        Interlocked.Increment(ref _waiting);
        var startedAt = Stopwatch.GetTimestamp();
        var acquired = false;
        try
        {
            if (_gate.Wait(0, cancellationToken))
            {
                acquired = true;
            }
            else
            {
                Interlocked.Increment(ref _contended);
                await _gate.WaitAsync(cancellationToken);
                acquired = true;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        var waitTicks = Stopwatch.GetTimestamp() - startedAt;
        Interlocked.Add(ref _totalWaitTicks, waitTicks);
        UpdateMaximum(ref _maximumWaitTicks, waitTicks);

        if (!acquired)
        {
            throw new InvalidOperationException("The merge coordinator failed to acquire its gate.");
        }

        var active = Interlocked.Increment(ref _active);
        try
        {
            if (active != 1)
            {
                throw new InvalidOperationException("More than one merge entered the serialized gate.");
            }

            return await merge(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _serialized);
            _gate.Release();
        }
    }

    public MergeContentionSnapshot Snapshot()
    {
        var totalWaitTicks = Interlocked.Read(ref _totalWaitTicks);
        var maximumWaitTicks = Interlocked.Read(ref _maximumWaitTicks);
        return new MergeContentionSnapshot(
            Interlocked.Read(ref _enqueued),
            Interlocked.Read(ref _serialized),
            Interlocked.Read(ref _contended),
            Volatile.Read(ref _waiting),
            Volatile.Read(ref _active),
            Stopwatch.GetElapsedTime(0, totalWaitTicks),
            Stopwatch.GetElapsedTime(0, maximumWaitTicks));
    }

    public void Dispose() => _gate.Dispose();

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
