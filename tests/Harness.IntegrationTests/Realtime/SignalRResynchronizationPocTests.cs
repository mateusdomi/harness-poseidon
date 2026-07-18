using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Realtime;
using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Messaging;
using Harness.Persistence.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Realtime;

public sealed class SignalRResynchronizationPocTests
{
    private const string Stream = $"project:{FoundationTransactionBehavior.ProjectId}";

    [Fact]
    public async Task PersistedOutboxResynchronizesHttpAndSignalRAfterHostRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-host-persisted-realtime",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(artifactRoot, "host.db");
        Directory.CreateDirectory(artifactRoot);
        var received = new ConcurrentQueue<RealtimeEventEnvelope>();

        try
        {
            await RunFirstHostAsync(databasePath, received, timeout.Token);
            await PrepareReplayAfterShutdownAsync(databasePath, timeout.Token);
            await RunRestartedHostAsync(databasePath, received, timeout.Token);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static async Task RunFirstHostAsync(
        string databasePath,
        ConcurrentQueue<RealtimeEventEnvelope> received,
        CancellationToken cancellationToken)
    {
        await using var app = CreateHost(databasePath);
        await app.StartAsync(cancellationToken);
        try
        {
            var baseAddress = GetBaseAddress(app.Services);
            var dispatcher = app.Services.GetRequiredService<SqliteWriteDispatcher>();
            await using (var connection = CreateConnection(baseAddress, received))
            {
                await connection.StartAsync(cancellationToken);
                var acknowledgement = await connection.InvokeAsync<EventSubscriptionAck>(
                    "Subscribe",
                    new[] { Stream },
                    cancellationToken);
                Assert.Equal([Stream], acknowledgement.Streams);

                await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                    FoundationTransactionBehavior.Command(),
                    cancellationToken);
                await WaitForCountAsync(received, 1, cancellationToken);
                Assert.Equal(1, received.Single().Sequence);
                await connection.StopAsync(cancellationToken);
            }

            await InsertOutboxAsync(
                dispatcher,
                "01ARZ3NDEKTSV4RRFFQ69G5FB1",
                "task.created",
                $"{{\"projectId\":\"{FoundationTransactionBehavior.ProjectId}\",\"taskId\":\"01ARZ3NDEKTSV4RRFFQ69G5FB4\"}}",
                cancellationToken);
            await InsertOutboxAsync(
                dispatcher,
                "01ARZ3NDEKTSV4RRFFQ69G5FB2",
                "progress.updated",
                $"{{\"projectId\":\"{FoundationTransactionBehavior.ProjectId}\",\"executed\":50}}",
                cancellationToken);
            await WaitForRealtimeSequenceAsync(app.Services, 3, cancellationToken);

            using var client = new HttpClient { BaseAddress = baseAddress };
            using var invalidResponse = await client.GetAsync(
                $"/api/v1/event-streams/snapshot?stream={Uri.EscapeDataString(Stream)}&afterSequence=-1",
                cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            Assert.Equal("application/problem+json", invalidResponse.Content.Headers.ContentType?.MediaType);

            var snapshot = await ReadHttpSnapshotAsync(client, 1, cancellationToken);
            Assert.Equal(3, snapshot.Sequence);
            Assert.Equal([2L, 3L], snapshot.Delta.Select(item => item.Sequence));
            Assert.Single(received);
        }
        finally
        {
            await app.StopAsync(cancellationToken);
        }
    }

    private static async Task PrepareReplayAfterShutdownAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
            databasePath,
            cancellationToken);
        Assert.Equal(0, await SqliteMigrationRunner.ApplyAsync(dispatcher, cancellationToken));
        await dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE outbox_messages
                    SET dispatched_at=NULL,available_at=$availableAt,lock_owner=NULL,lock_expires_at=NULL
                    WHERE id=$messageId;
                    """;
                command.Parameters.AddWithValue(
                    "$availableAt",
                    DateTimeOffset.UtcNow.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(
                    "$messageId",
                    FoundationTransactionBehavior.Command().OutboxMessageId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
            },
            cancellationToken);
    }

    private static async Task RunRestartedHostAsync(
        string databasePath,
        ConcurrentQueue<RealtimeEventEnvelope> received,
        CancellationToken cancellationToken)
    {
        await using var app = CreateHost(databasePath);
        await app.StartAsync(cancellationToken);
        try
        {
            var baseAddress = GetBaseAddress(app.Services);
            await using var connection = CreateConnection(baseAddress, received);
            await connection.StartAsync(cancellationToken);
            await connection.InvokeAsync<EventSubscriptionAck>(
                "Subscribe",
                new[] { Stream },
                cancellationToken);

            var hubSnapshot = await connection.InvokeAsync<EventStreamSnapshot>(
                "GetStreamSnapshot",
                Stream,
                1L,
                cancellationToken);
            Assert.Equal(3, hubSnapshot.Sequence);
            Assert.Equal([2L, 3L], hubSnapshot.Delta.Select(item => item.Sequence));

            await WaitForOutboxAsync(app.Services, dispatched: 3, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            Assert.Single(received);

            var dispatcher = app.Services.GetRequiredService<SqliteWriteDispatcher>();
            await InsertOutboxAsync(
                dispatcher,
                "01ARZ3NDEKTSV4RRFFQ69G5FB3",
                "task.stateChanged",
                $"{{\"projectId\":\"{FoundationTransactionBehavior.ProjectId}\",\"state\":\"done\"}}",
                cancellationToken);
            await WaitForCountAsync(received, 2, cancellationToken);

            Assert.Equal([1L, 4L], received.Select(item => item.Sequence));
            using var client = new HttpClient { BaseAddress = baseAddress };
            var finalSnapshot = await ReadHttpSnapshotAsync(client, 0, cancellationToken);
            Assert.Equal(4, finalSnapshot.Sequence);
            Assert.Equal([1L, 2L, 3L, 4L], finalSnapshot.Delta.Select(item => item.Sequence));
            Assert.Equal(4, finalSnapshot.LatestByType["task.stateChanged"].Sequence);
            await WaitForOutboxAsync(app.Services, dispatched: 4, cancellationToken);
        }
        finally
        {
            await app.StopAsync(cancellationToken);
        }
    }

    private static WebApplication CreateHost(string databasePath) =>
        HostApplication.Build(
            [
                "--urls", "http://127.0.0.1:0",
                "--environment", "Development",
                "--Harness:DatabasePath", databasePath,
            ]);

    private static async Task InsertOutboxAsync(
        SqliteWriteDispatcher dispatcher,
        string messageId,
        string eventType,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO outbox_messages
                        (id,tenant_id,event_type,payload_json,occurred_at,available_at)
                    VALUES ($id,$tenantId,$eventType,$payload,$occurredAt,$occurredAt);
                    """;
                command.Parameters.AddWithValue("$id", messageId);
                command.Parameters.AddWithValue("$tenantId", FoundationTransactionBehavior.TenantId);
                command.Parameters.AddWithValue("$eventType", eventType);
                command.Parameters.AddWithValue("$payload", payloadJson);
                command.Parameters.AddWithValue(
                    "$occurredAt",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    private static async Task<EventStreamSnapshot> ReadHttpSnapshotAsync(
        HttpClient client,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
            $"/api/v1/event-streams/snapshot?stream={Uri.EscapeDataString(Stream)}&afterSequence={afterSequence}",
            cancellationToken);
        return snapshot ?? throw new InvalidOperationException("Snapshot response was empty.");
    }

    private static async Task WaitForRealtimeSequenceAsync(
        IServiceProvider services,
        long sequence,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<Harness.Persistence.Abstractions.Realtime.IRealtimeEventStore>();
        while ((await store.ReadSnapshotAsync(Stream, 0, cancellationToken)).Sequence < sequence)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static async Task WaitForOutboxAsync(
        IServiceProvider services,
        long dispatched,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IOutboxStore>();
        while ((await store.ReadSnapshotAsync(cancellationToken)).Dispatched < dispatched)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static HubConnection CreateConnection(
        Uri baseAddress,
        ConcurrentQueue<RealtimeEventEnvelope> received)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "/hubs/events"))
            .Build();
        connection.On<RealtimeEventEnvelope>("event", received.Enqueue);
        return connection;
    }

    private static Uri GetBaseAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish a server address.");
        var address = addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return new Uri(address, UriKind.Absolute);
    }

    private static async Task WaitForCountAsync(
        ConcurrentQueue<RealtimeEventEnvelope> received,
        int count,
        CancellationToken cancellationToken)
    {
        while (received.Count < count)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }
}
