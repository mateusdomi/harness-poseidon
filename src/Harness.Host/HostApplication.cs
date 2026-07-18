using System.Net;
using Harness.Host.Agents;
using Harness.Host.Conversations;
using Harness.Host.Documents;
using Harness.Host.Ipc;
using Harness.Host.Organizations;
using Harness.Host.Persistence;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Host.Workers;
using Harness.Host.WorkBoard;
using Harness.Host.Workflows;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Cockpit;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Messaging;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Realtime;
using Harness.Persistence.Abstractions.RunnerIpc;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;

namespace Harness.Host;

public static class HostApplication
{
    public static WebApplication Build(
        string[] args,
        RunnerIpcToken? runnerIpcToken = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);

        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton(runnerIpcToken ?? RunnerIpcToken.Create());
        var databasePath = builder.Configuration["Harness:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(AppContext.BaseDirectory, "data", "harness.db");
        }

        builder.Services.AddSingleton(
            _ => SqliteWriteDispatcher.CreateAsync(databasePath).GetAwaiter().GetResult());
        builder.Services.AddSingleton<IHostedService, SqliteMigrationHostedService>();
        builder.Services.AddSingleton<IRunnerMessageStore>(services =>
            new SqliteRunnerMessageStore(services.GetRequiredService<SqliteWriteDispatcher>()));
        builder.Services.AddSingleton<IOutboxStore, SqliteOutboxStore>();
        builder.Services.AddSingleton<IRealtimeEventStore, SqliteRealtimeEventStore>();
        builder.Services.AddSingleton<IDurableExecutionEngine, SqliteDurableExecutionEngine>();
        builder.Services.AddSingleton<ILocalProfileStore, SqliteLocalProfileStore>();
        builder.Services.AddSingleton<IOrganizationStore, SqliteOrganizationStore>();
        builder.Services.AddSingleton<IProjectStore, SqliteProjectStore>();
        builder.Services.AddSingleton<IAgentCatalogStore, SqliteAgentCatalogStore>();
        builder.Services.AddSingleton<ICockpitDigestStore, SqliteCockpitDigestStore>();
        builder.Services.AddSingleton<IConversationStore, SqliteConversationStore>();
        builder.Services.AddSingleton<IWorkChainStore, SqliteWorkChainStore>();
        builder.Services.AddSingleton<IWorkBoardStore, SqliteWorkBoardStore>();
        builder.Services.AddSingleton<IWorkflowStore, SqliteWorkflowStore>();
        builder.Services.AddSingleton<IWorkflowCatalogStore, SqliteWorkflowCatalogStore>();
        builder.Services.AddSingleton<IDocumentStore, SqliteDocumentStore>();
        builder.Services.AddSingleton<IDocumentCatalogStore, SqliteDocumentCatalogStore>();
        var documentCatalogPath = builder.Configuration["Harness:DocumentCatalogPath"];
        if (string.IsNullOrWhiteSpace(documentCatalogPath))
        {
            documentCatalogPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "catalog");
        }
        builder.Services.AddSingleton<IDocumentContentCatalog>(
            new FileSystemDocumentContentCatalog(documentCatalogPath));
        builder.Services.AddSingleton<OutboxRealtimeStreamResolver>();
        builder.Services.AddSingleton<IRealtimeEventBroadcaster, SignalRRealtimeEventBroadcaster>();
        builder.Services.AddSingleton<IOutboxMessageSink, PersistedRealtimeOutboxSink>();
        builder.Services.AddSingleton(
            new OutboxDispatcherOptions(
                $"host-outbox-{Guid.NewGuid():N}",
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(100),
                100,
                new OutboxRetryPolicy(
                    5,
                    TimeSpan.FromSeconds(1),
                    2m,
                    TimeSpan.FromMinutes(1))));
        builder.Services.AddHostedService<OutboxDispatcherBackgroundService>();
        builder.Services.AddSingleton(
            new DurableExecutionWatchdogOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(2)));
        builder.Services.AddHostedService<DurableExecutionWatchdogBackgroundService>();
        builder.Services.AddSingleton<EventPublisher>();
        builder.Services.AddSingleton<RunnerIpcMessageProcessor>();
        builder.Services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());
        builder.Services.AddOpenApi(options =>
            options.AddDocumentTransformer(
                (document, _, _) =>
                {
                    document.Info.Title = "Harness API";
                    document.Servers?.Clear();
                    return Task.CompletedTask;
                }));

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
            .WithTags("system");
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapHub<EventsHub>("/hubs/events");
        app.MapRunnerIpc();
        app.MapLocalProfiles();
        app.MapOrganizations();
        app.MapProjects();
        app.MapAgents();
        app.MapConversations();
        app.MapWorkBoard();
        app.MapWorkflowCatalog();
        app.MapDocumentCatalog();
        app.MapGet(
            "/api/v1/event-streams/snapshot",
            async Task<IResult> (
                string? stream,
                long? afterSequence,
                IRealtimeEventStore store,
                CancellationToken cancellationToken) =>
            {
                if (!EventStreamName.IsValid(stream))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "invalid_event_stream",
                        detail: "A valid stream query parameter is required.");
                }

                var cursor = afterSequence ?? 0;
                if (cursor < 0)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "invalid_event_sequence",
                        detail: "afterSequence cannot be negative.");
                }

                var snapshot = await store.ReadSnapshotAsync(
                    stream!,
                    cursor,
                    cancellationToken);
                return Results.Ok(RealtimeSnapshotMapper.ToContract(snapshot));
            })
            .WithTags("events")
            .Produces<EventStreamSnapshot>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private sealed record HealthResponse(string Status);
}
