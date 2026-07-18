using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Realtime;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Realtime;

public sealed class SignalRResynchronizationPocTests
{
    private const string Stream = "project:01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public async Task DisconnectGapIsRecoveredBySnapshotDeltaBeforeLiveStreamContinues()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var app = HostApplication.Build(
            ["--urls", "http://127.0.0.1:0", "--environment", "Development"]);
        await app.StartAsync(timeout.Token);

        try
        {
            var baseAddress = GetBaseAddress(app.Services);
            var publisher = app.Services.GetRequiredService<EventPublisher>();
            var received = new ConcurrentQueue<RealtimeEventEnvelope>();

            await using (var connection = CreateConnection(baseAddress, received))
            {
                await connection.StartAsync(timeout.Token);
                var acknowledgement = await connection.InvokeAsync<EventSubscriptionAck>(
                    "Subscribe",
                    new[] { Stream },
                    timeout.Token);
                Assert.Equal([Stream], acknowledgement.Streams);

                await publisher.PublishAsync(Stream, "task.stateChanged", new { state = "ready" }, timeout.Token);
                await publisher.PublishAsync(Stream, "attempt.started", new { attemptId = "attempt-1" }, timeout.Token);
                await WaitForCountAsync(received, 2, timeout.Token);
                await connection.StopAsync(timeout.Token);
            }

            await publisher.PublishAsync(Stream, "attempt.heartbeat", new { attemptId = "attempt-1" }, timeout.Token);
            await publisher.PublishAsync(Stream, "gate.changed", new { gate = "tests", state = "green" }, timeout.Token);
            await publisher.PublishAsync(Stream, "progress.updated", new { executed = 2, validated = 1 }, timeout.Token);

            using var client = new HttpClient { BaseAddress = baseAddress };
            using var invalidResponse = await client.GetAsync(
                $"/api/v1/event-streams/snapshot?stream={Uri.EscapeDataString(Stream)}&afterSequence=-1",
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            Assert.Equal("application/problem+json", invalidResponse.Content.Headers.ContentType?.MediaType);

            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                $"/api/v1/event-streams/snapshot?stream={Uri.EscapeDataString(Stream)}&afterSequence=2",
                timeout.Token);
            Assert.NotNull(snapshot);
            Assert.Equal(5, snapshot.Sequence);
            Assert.Equal([3L, 4L, 5L], snapshot.Delta.Select(item => item.Sequence));
            Assert.Equal(5, snapshot.LatestByType["progress.updated"].Sequence);
            Assert.Equal(2, received.Count);

            await using var resumedConnection = CreateConnection(baseAddress, received);
            await resumedConnection.StartAsync(timeout.Token);
            await resumedConnection.InvokeAsync<EventSubscriptionAck>(
                "Subscribe",
                new[] { Stream },
                timeout.Token);
            await publisher.PublishAsync(Stream, "task.stateChanged", new { state = "done" }, timeout.Token);
            await WaitForCountAsync(received, 3, timeout.Token);

            Assert.Equal([1L, 2L, 6L], received.Select(item => item.Sequence));
        }
        finally
        {
            await app.StopAsync(timeout.Token);
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
        var address = addresses.Single(item => item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
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
