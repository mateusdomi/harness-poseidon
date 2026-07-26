using System.Collections.Concurrent;
using System.Diagnostics;
using Harness.Host.Observability;
using Harness.Host.Workers;
using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Messaging;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Workers;

public sealed class OutboxDispatcherBackgroundServiceTests
{
    [Fact]
    public async Task DispatchEmitsCorrelatedSpansWithoutPayloadOrFailureText()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await SqliteOutboxFixture.CreateAsync(2, timeout.Token);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PoseidonTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var clock = new MutableClock(
            new DateTimeOffset(2026, 7, 18, 18, 20, 0, TimeSpan.Zero));
        var worker = CreateWorker(fixture.Store, new FailFirstSink(), clock, "telemetry-worker");

        using var correlation = new Activity("outbox-telemetry-test").Start();
        Assert.NotNull(correlation);
        Assert.Equal(2, await worker.DispatchAvailableAsync(timeout.Token));

        var spans = activities
            .Where(activity =>
                activity.OperationName == "poseidon.outbox.dispatch" &&
                activity.TraceId == correlation.TraceId)
            .ToArray();
        Assert.Equal(2, spans.Length);
        Assert.All(spans, span =>
        {
            Assert.NotNull(span.GetTagItem("tenant_id"));
            Assert.NotNull(span.GetTagItem("message_id"));
            Assert.DoesNotContain(
                span.TagObjects,
                tag => tag.Key.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
                       tag.Key.Contains("exception.message", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains(spans, span => Equals(span.GetTagItem("outbox.result"), "retry_scheduled"));
        Assert.Contains(spans, span => Equals(span.GetTagItem("outbox.result"), "applied"));
    }

    [Fact]
    public async Task FailureRetriesAfterRestartWithoutDuplicatingSuccessfulMessage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await SqliteOutboxFixture.CreateAsync(2, timeout.Token);
        var clock = new MutableClock(
            new DateTimeOffset(2026, 7, 18, 18, 30, 0, TimeSpan.Zero));
        var failingSink = new FailFirstSink();
        var firstWorker = CreateWorker(fixture.Store, failingSink, clock, "worker-before-restart");

        Assert.Equal(2, await firstWorker.DispatchAvailableAsync(timeout.Token));
        Assert.Equal(
            new OutboxStoreSnapshot(1, 0, 1, 0, 1),
            await fixture.Store.ReadSnapshotAsync(timeout.Token));
        Assert.Single(failingSink.DispatchedMessageIds);

        clock.Advance(TimeSpan.FromSeconds(1));
        var resumedSink = new RecordingSink();
        var resumedWorker = CreateWorker(fixture.Store, resumedSink, clock, "worker-after-restart");
        Assert.Equal(1, await resumedWorker.DispatchAvailableAsync(timeout.Token));
        Assert.Equal(
            new OutboxStoreSnapshot(0, 0, 2, 0, 1),
            await fixture.Store.ReadSnapshotAsync(timeout.Token));
        Assert.Single(resumedSink.DispatchedMessageIds);
        Assert.DoesNotContain(
            resumedSink.DispatchedMessageIds[0],
            failingSink.DispatchedMessageIds);
    }

    [Fact]
    public async Task CancellationLeavesClaimForExpiryAndNextInstanceRecoversIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await SqliteOutboxFixture.CreateAsync(1, timeout.Token);
        var clock = new MutableClock(
            new DateTimeOffset(2026, 7, 18, 18, 40, 0, TimeSpan.Zero));
        var blockingSink = new BlockingSink();
        var interrupted = CreateWorker(fixture.Store, blockingSink, clock, "interrupted-worker");

        await interrupted.StartAsync(timeout.Token);
        await blockingSink.Entered.WaitAsync(timeout.Token);
        await interrupted.StopAsync(timeout.Token);
        Assert.Equal(
            new OutboxStoreSnapshot(0, 1, 0, 0, 0),
            await fixture.Store.ReadSnapshotAsync(timeout.Token));

        clock.Advance(TimeSpan.FromMinutes(2));
        var recoverySink = new RecordingSink();
        var recovery = CreateWorker(fixture.Store, recoverySink, clock, "recovery-worker");
        Assert.Equal(1, await recovery.DispatchAvailableAsync(timeout.Token));
        Assert.Equal(
            new OutboxStoreSnapshot(0, 0, 1, 0, 0),
            await fixture.Store.ReadSnapshotAsync(timeout.Token));
        Assert.Single(recoverySink.DispatchedMessageIds);
    }

    private static OutboxDispatcherBackgroundService CreateWorker(
        IOutboxStore store,
        IOutboxMessageSink sink,
        IClock clock,
        string owner) =>
        new(
            store,
            sink,
            clock,
            new OutboxDispatcherOptions(
                owner,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMilliseconds(10),
                MaximumBatchSize: 10,
                new OutboxRetryPolicy(
                    MaximumAttempts: 3,
                    TimeSpan.FromSeconds(1),
                    Multiplier: 2m,
                    TimeSpan.FromSeconds(10))));

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private class RecordingSink : IOutboxMessageSink
    {
        public List<string> DispatchedMessageIds { get; } = [];

        public virtual Task DispatchAsync(
            OutboxLease message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DispatchedMessageIds.Add(message.MessageId);
            return Task.CompletedTask;
        }
    }

    private sealed class FailFirstSink : RecordingSink
    {
        private int _attempts;

        public override Task DispatchAsync(
            OutboxLease message,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new InvalidOperationException("The message text must not be persisted.");
            }

            return base.DispatchAsync(message, cancellationToken);
        }
    }

    private sealed class BlockingSink : IOutboxMessageSink
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task DispatchAsync(
            OutboxLease message,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class SqliteOutboxFixture(
        string artifactRoot,
        SqliteWriteDispatcher dispatcher,
        SqliteOutboxStore store) : IAsyncDisposable
    {
        public SqliteOutboxStore Store { get; } = store;

        public static async Task<SqliteOutboxFixture> CreateAsync(
            int messageCount,
            CancellationToken cancellationToken)
        {
            var artifactRoot = Path.Combine(
                AppContext.BaseDirectory,
                "poc-artifacts",
                "f1-outbox-worker",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(artifactRoot);
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "outbox.db"),
                cancellationToken);
            try
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, cancellationToken);
                var foundation = new SqliteFoundationTransactionStore(dispatcher);
                await foundation.ProvisionProjectAsync(
                    FoundationTransactionBehavior.Command(),
                    cancellationToken);
                if (messageCount == 2)
                {
                    await foundation.ProvisionProjectAsync(
                        OutboxStoreBehavior.SecondProjectCommand(),
                        cancellationToken);
                }

                return new SqliteOutboxFixture(
                    artifactRoot,
                    dispatcher,
                    new SqliteOutboxStore(dispatcher));
            }
            catch
            {
                await dispatcher.DisposeAsync();
                Directory.Delete(artifactRoot, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await dispatcher.DisposeAsync();
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }
}
