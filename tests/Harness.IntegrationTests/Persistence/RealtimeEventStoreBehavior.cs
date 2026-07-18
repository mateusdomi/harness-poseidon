using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Realtime;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

internal static class RealtimeEventStoreBehavior
{
    private const string Stream = "project:01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private static readonly DateTimeOffset BaseTime =
        new(2026, 7, 18, 17, 40, 0, TimeSpan.Zero);

    public static async Task AssertAsync(
        IRealtimeEventStore store,
        CancellationToken cancellationToken)
    {
        var empty = await store.ReadSnapshotAsync(Stream, 0, cancellationToken);
        Assert.Equal(0, empty.Sequence);
        Assert.Empty(empty.LatestByType);
        Assert.Empty(empty.Delta);

        var commands = Enumerable.Range(0, 10)
            .Select(index => Command(
                index,
                index == 0 ? "task.created" : "task.stateChanged"))
            .ToArray();
        var concurrent = await Task.WhenAll(
            commands.Select(command => store.AppendAsync(command, cancellationToken)));
        Assert.Equal(Enumerable.Range(1, 10).Select(value => (long)value),
            concurrent.Select(receipt => receipt.Sequence).Order());
        Assert.All(concurrent, receipt => Assert.False(receipt.Replay));

        var replay = await store.AppendAsync(commands[0], cancellationToken);
        Assert.True(replay.Replay);
        Assert.Equal(
            concurrent.Single(receipt => receipt.MessageId == commands[0].MessageId).Sequence,
            replay.Sequence);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.AppendAsync(
                commands[0] with { PayloadJson = "{\"state\":\"conflict\"}" },
                cancellationToken));

        var latestCreated = await store.AppendAsync(
            Command(10, "task.created"),
            cancellationToken);
        Assert.Equal(11, latestCreated.Sequence);

        var snapshot = await store.ReadSnapshotAsync(Stream, 7, cancellationToken);
        Assert.Equal(11, snapshot.Sequence);
        Assert.Equal([8L, 9L, 10L, 11L], snapshot.Delta.Select(item => item.Sequence));
        Assert.Equal(2, snapshot.LatestByType.Count);
        Assert.Equal(11, snapshot.LatestByType["task.created"].Sequence);
        Assert.Equal(
            concurrent
                .Where(receipt => receipt.EventType == "task.stateChanged")
                .Max(receipt => receipt.Sequence),
            snapshot.LatestByType["task.stateChanged"].Sequence);

        var complete = await store.ReadSnapshotAsync(Stream, 0, cancellationToken);
        Assert.Equal(11, complete.Delta.Count);
        Assert.Equal(11, complete.Delta.Select(item => item.MessageId).Distinct().Count());
    }

    private static RealtimeEventAppendCommand Command(int index, string eventType) =>
        new(
            UlidValue.New(BaseTime.AddMilliseconds(index + 100)).ToString(),
            FoundationTransactionBehavior.TenantId,
            Stream,
            eventType,
            $"{{\"index\":{index},\"state\":\"ready\"}}",
            BaseTime.AddMilliseconds(index));
}
