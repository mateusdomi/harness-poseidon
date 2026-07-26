namespace Harness.Modules.Coordination.Application;

public enum CardPriority
{
    High = 0,
    Normal = 1,
    Low = 2
}

public sealed record QueuedCardItem(
    string CardId,
    string Role,
    CardPriority Priority,
    DateTimeOffset EnqueuedAt);

public sealed record ScaleDispatchResult(
    IReadOnlyList<string> DispatchedWorkerCards,
    IReadOnlyList<string> DispatchedCriticCards,
    int DeferredCount,
    string DispatchReason);

public sealed class CardPrioritizedBuffer
{
    private readonly object _sync = new();
    private readonly PriorityQueue<QueuedCardItem, (int Priority, long Sequence)> _queue = new();
    private long _sequenceCounter;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public void Enqueue(string cardId, string role, CardPriority priority, DateTimeOffset enqueuedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        var item = new QueuedCardItem(cardId, role, priority, enqueuedAt);
        var sequence = Interlocked.Increment(ref _sequenceCounter);
        lock (_sync)
        {
            _queue.Enqueue(item, ((int)priority, sequence));
        }
    }

    public QueuedCardItem? Dequeue()
    {
        lock (_sync)
        {
            return _queue.Count > 0 ? _queue.Dequeue() : null;
        }
    }

    public IReadOnlyList<QueuedCardItem> PeekAll()
    {
        lock (_sync)
        {
            return _queue.UnorderedItems
                .Select(item => item.Element)
                .OrderBy(item => (int)item.Priority)
                .ThenBy(item => item.EnqueuedAt)
                .ToArray();
        }
    }
}

public interface IScaleDispatcher
{
    ScaleDispatchResult Dispatch(
        CardPrioritizedBuffer buffer,
        int globalMaxConcurrency,
        int currentRunningCount);
}

public sealed class ScaleDispatcher : IScaleDispatcher
{
    public ScaleDispatchResult Dispatch(
        CardPrioritizedBuffer buffer,
        int globalMaxConcurrency,
        int currentRunningCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(globalMaxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(currentRunningCount);

        if (currentRunningCount >= globalMaxConcurrency)
        {
            return new ScaleDispatchResult(
                DispatchedWorkerCards: [],
                DispatchedCriticCards: [],
                DeferredCount: buffer.Count,
                DispatchReason: "global_concurrency_limit_reached");
        }

        var availableSlots = globalMaxConcurrency - currentRunningCount;
        var workers = new List<string>();
        var critics = new List<string>();

        while (availableSlots > 0 && buffer.Count > 0)
        {
            var item = buffer.Dequeue();
            if (item is null) break;

            if (string.Equals(item.Role, "critic", StringComparison.OrdinalIgnoreCase))
            {
                critics.Add(item.CardId);
            }
            else
            {
                workers.Add(item.CardId);
            }

            availableSlots--;
        }

        return new ScaleDispatchResult(
            DispatchedWorkerCards: workers,
            DispatchedCriticCards: critics,
            DeferredCount: buffer.Count,
            DispatchReason: "dispatched_successfully");
    }
}
