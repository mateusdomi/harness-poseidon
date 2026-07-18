using Harness.Host.Realtime;
using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Messaging;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Realtime;

public sealed class PersistedRealtimeOutboxSinkTests
{
    [Fact]
    public async Task RestartReplayKeepsSequenceAndDoesNotBroadcastTwice()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-persisted-realtime-sink",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(artifactRoot, "realtime.db");
        Directory.CreateDirectory(artifactRoot);
        var message = new OutboxLease(
            "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            FoundationTransactionBehavior.TenantId,
            "project.created",
            $"{{\"projectId\":\"{FoundationTransactionBehavior.ProjectId}\"}}",
            new DateTimeOffset(2026, 7, 18, 17, 50, 0, TimeSpan.Zero),
            0,
            "test-owner",
            1,
            new DateTimeOffset(2026, 7, 18, 17, 51, 0, TimeSpan.Zero));

        try
        {
            var firstBroadcaster = new RecordingBroadcaster();
            await using (var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                databasePath,
                timeout.Token))
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                    FoundationTransactionBehavior.Command(),
                    timeout.Token);
                var sink = CreateSink(dispatcher, firstBroadcaster);
                await sink.DispatchAsync(message, timeout.Token);
            }

            var secondBroadcaster = new RecordingBroadcaster();
            await using (var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                databasePath,
                timeout.Token))
            {
                Assert.Equal(0, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
                var sink = CreateSink(dispatcher, secondBroadcaster);
                await sink.DispatchAsync(message, timeout.Token);
                var snapshot = await new SqliteRealtimeEventStore(dispatcher).ReadSnapshotAsync(
                    $"project:{FoundationTransactionBehavior.ProjectId}",
                    0,
                    timeout.Token);
                Assert.Equal(1, snapshot.Sequence);
                Assert.Single(snapshot.Delta);
                Assert.Equal(message.MessageId, snapshot.Delta[0].MessageId);
            }

            var first = Assert.Single(firstBroadcaster.Events);
            Assert.Equal(1, first.Sequence);
            Assert.Empty(secondBroadcaster.Events);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolverUsesTenantFallbackForLegacyPayload()
    {
        var message = new OutboxLease(
            "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            FoundationTransactionBehavior.TenantId,
            "task.created",
            "{\"taskId\":\"01ARZ3NDEKTSV4RRFFQ69G5FB1\"}",
            new DateTimeOffset(2026, 7, 18, 17, 50, 0, TimeSpan.Zero),
            0,
            "test-owner",
            1,
            new DateTimeOffset(2026, 7, 18, 17, 51, 0, TimeSpan.Zero));

        Assert.Equal(
            $"tenant:{FoundationTransactionBehavior.TenantId}",
            new OutboxRealtimeStreamResolver().Resolve(message));
    }

    private static PersistedRealtimeOutboxSink CreateSink(
        SqliteWriteDispatcher dispatcher,
        IRealtimeEventBroadcaster broadcaster) =>
        new(
            new SqliteRealtimeEventStore(dispatcher),
            new OutboxRealtimeStreamResolver(),
            broadcaster);

    private sealed class RecordingBroadcaster : IRealtimeEventBroadcaster
    {
        public List<RealtimeEventEnvelope> Events { get; } = [];

        public Task BroadcastAsync(
            RealtimeEventEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(envelope);
            return Task.CompletedTask;
        }
    }
}
