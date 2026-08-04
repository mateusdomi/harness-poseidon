using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Messaging;

namespace Harness.IntegrationTests.Persistence;

internal static class OutboxStoreBehavior
{
    public static async Task AssertAsync(
        IOutboxStore store,
        CancellationToken cancellationToken)
    {
        var now = new DateTimeOffset(2026, 7, 18, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new OutboxStoreSnapshot(2, 0, 0, 0, 0)
            {
                OldestPendingAge = now - new DateTimeOffset(2026, 7, 18, 13, 40, 0, TimeSpan.Zero),
            },
            await store.ReadSnapshotAsync(now, cancellationToken));
        var acquisitions = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(index =>
                store.TryAcquireNextAsync(
                    $"dispatcher-{index:D2}",
                    TimeSpan.FromMinutes(1),
                    now,
                    cancellationToken)));
        var initialLeases = acquisitions
            .Where(lease => lease is not null)
            .Cast<OutboxLease>()
            .OrderBy(lease => lease.OccurredAt)
            .ToArray();
        Assert.Equal(2, initialLeases.Length);
        Assert.Equal(2, initialLeases.Select(lease => lease.MessageId).Distinct().Count());
        Assert.All(initialLeases, lease =>
        {
            Assert.Equal(1, lease.FencingToken);
            Assert.Equal(0, lease.Attempts);
            Assert.Equal(now.AddMinutes(1), lease.LockExpiresAt);
            Assert.StartsWith("dispatcher-", lease.Owner, StringComparison.Ordinal);
        });
        Assert.Equal(
            new OutboxStoreSnapshot(0, 2, 0, 0, 0),
            await store.ReadSnapshotAsync(now, cancellationToken));

        var recoveryTime = now.AddMinutes(2);
        Assert.Equal(2, await store.ReleaseExpiredClaimsAsync(recoveryTime, cancellationToken));
        var retryTarget = Assert.IsType<OutboxLease>(await store.TryAcquireNextAsync(
            "recovery-a",
            TimeSpan.FromMinutes(1),
            recoveryTime,
            cancellationToken));
        var successTarget = Assert.IsType<OutboxLease>(await store.TryAcquireNextAsync(
            "recovery-b",
            TimeSpan.FromMinutes(1),
            recoveryTime,
            cancellationToken));
        Assert.NotEqual(retryTarget.MessageId, successTarget.MessageId);
        Assert.Equal(2, retryTarget.FencingToken);
        Assert.Equal(2, successTarget.FencingToken);

        var staleLease = initialLeases.Single(lease => lease.MessageId == retryTarget.MessageId);
        Assert.Equal(
            OutboxMutationStatus.LeaseRejected,
            (await store.MarkDispatchedAsync(
                new OutboxDispatchCommand(
                    staleLease.MessageId,
                    staleLease.Owner,
                    staleLease.FencingToken,
                    recoveryTime.AddSeconds(5)),
                cancellationToken)).Status);

        var dispatchResult = await store.MarkDispatchedAsync(
            new OutboxDispatchCommand(
                successTarget.MessageId,
                successTarget.Owner,
                successTarget.FencingToken,
                recoveryTime.AddSeconds(5)),
            cancellationToken);
        Assert.Equal(OutboxMutationStatus.Applied, dispatchResult.Status);

        var policy = new OutboxRetryPolicy(
            MaximumAttempts: 2,
            TimeSpan.FromSeconds(1),
            Multiplier: 2m,
            TimeSpan.FromSeconds(10));
        var firstFailureTime = recoveryTime.AddSeconds(10);
        var firstFailure = await store.RecordFailureAsync(
            new OutboxFailureCommand(
                retryTarget.MessageId,
                "01ARZ3NDEKTSV4RRFFQ69G5FD1",
                retryTarget.Owner,
                retryTarget.FencingToken,
                "Transient transport failure",
                policy,
                firstFailureTime),
            cancellationToken);
        Assert.Equal(OutboxMutationStatus.Applied, firstFailure.Status);
        Assert.Equal(1, firstFailure.Attempts);
        Assert.Equal(firstFailureTime.AddSeconds(1), firstFailure.AvailableAt);
        Assert.Null(await store.TryAcquireNextAsync(
            "too-early",
            TimeSpan.FromMinutes(1),
            firstFailureTime.AddMilliseconds(500),
            cancellationToken));

        var retryLease = Assert.IsType<OutboxLease>(await store.TryAcquireNextAsync(
            "retry-owner",
            TimeSpan.FromMinutes(1),
            firstFailure.AvailableAt!.Value,
            cancellationToken));
        Assert.Equal(retryTarget.MessageId, retryLease.MessageId);
        Assert.Equal(3, retryLease.FencingToken);
        Assert.Equal(1, retryLease.Attempts);
        Assert.Equal(
            OutboxMutationStatus.LeaseRejected,
            (await store.RecordFailureAsync(
                new OutboxFailureCommand(
                    retryTarget.MessageId,
                    "01ARZ3NDEKTSV4RRFFQ69G5FD2",
                    retryTarget.Owner,
                    retryTarget.FencingToken,
                    "Stale owner failure",
                    policy,
                    firstFailure.AvailableAt.Value.AddMilliseconds(100)),
                cancellationToken)).Status);

        var deadLetter = await store.RecordFailureAsync(
            new OutboxFailureCommand(
                retryLease.MessageId,
                "01ARZ3NDEKTSV4RRFFQ69G5FD3",
                retryLease.Owner,
                retryLease.FencingToken,
                "Permanent transport failure",
                policy,
                firstFailure.AvailableAt.Value.AddMilliseconds(500)),
            cancellationToken);
        Assert.Equal(OutboxMutationStatus.DeadLettered, deadLetter.Status);
        Assert.Equal(2, deadLetter.Attempts);
        Assert.Null(deadLetter.AvailableAt);

        var finalSnapshotNow = firstFailure.AvailableAt!.Value.AddMilliseconds(500);
        Assert.Equal(
            new OutboxStoreSnapshot(0, 0, 1, 1, 2)
            {
                DispatchedLastMinute = 1,
                DispatchedLastHour = 1,
            },
            await store.ReadSnapshotAsync(finalSnapshotNow, cancellationToken));
        Assert.Null(await store.TryAcquireNextAsync(
            "terminal-scan",
            TimeSpan.FromMinutes(1),
            recoveryTime.AddHours(1),
            cancellationToken));
        Assert.Equal(0, await store.ReleaseExpiredClaimsAsync(
            recoveryTime.AddHours(1),
            cancellationToken));
        Assert.Equal(
            OutboxMutationStatus.AlreadyTerminal,
            (await store.MarkDispatchedAsync(
                new OutboxDispatchCommand(
                    retryLease.MessageId,
                    retryLease.Owner,
                    retryLease.FencingToken,
                    recoveryTime.AddMinutes(1)),
                cancellationToken)).Status);
        Assert.Equal(
            OutboxMutationStatus.NotFound,
            (await store.MarkDispatchedAsync(
                new OutboxDispatchCommand(
                    "01ARZ3NDEKTSV4RRFFQ69G5FE1",
                    "missing-owner",
                    1,
                    recoveryTime.AddMinutes(1)),
                cancellationToken)).Status);
    }

    public static ProjectProvisionCommand SecondProjectCommand() => new(
        "01ARZ3NDEKTSV4RRFFQ69G5FC1",
        "Outbox Tenant",
        "01ARZ3NDEKTSV4RRFFQ69G5FC2",
        "Outbox Organization",
        "01ARZ3NDEKTSV4RRFFQ69G5FC3",
        "Outbox Project",
        "01ARZ3NDEKTSV4RRFFQ69G5FC4",
        "Outbox User",
        "foundation:outbox-second-project",
        new string('8', 64),
        "01ARZ3NDEKTSV4RRFFQ69G5FC5",
        "01ARZ3NDEKTSV4RRFFQ69G5FC6",
        "project.created",
        "{\"projectId\":\"01ARZ3NDEKTSV4RRFFQ69G5FC3\"}",
        new DateTimeOffset(2026, 7, 18, 13, 41, 0, TimeSpan.Zero));
}
