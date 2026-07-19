using System.Net;
using Harness.Host.Agents;
using Harness.Host.Conversations;
using Harness.Host.Documents;
using Harness.Host.Execution;
using Harness.Host.Ipc;
using Harness.Host.Licensing;
using Harness.Host.Governance;
using Harness.Host.Organizations;
using Harness.Host.Notifications;
using Harness.Host.Operations;
using Harness.Host.Persistence;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Providers;
using Harness.Host.Prototyping;
using Harness.Host.Realtime;
using Harness.Host.RunTargets;
using Harness.Host.Workers;
using Harness.Host.WorkBoard;
using Harness.Host.Workflows;
using Harness.Host.Tools;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Execution.Infrastructure.Sandbox;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Cockpit;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Licensing;
using Harness.Persistence.Abstractions.Messaging;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Notifications;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.Persistence.Abstractions.Realtime;
using Harness.Persistence.Abstractions.RunnerIpc;
using Harness.Persistence.Abstractions.RunTargets;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.Persistence.Abstractions.Tools;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.FileProviders;

namespace Harness.Host;

public static class HostApplication
{
    public static WebApplication Build(
        string[] args,
        RunnerIpcToken? runnerIpcToken = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);

        var frontendPath = ResolveFrontendPath(builder.Environment.ContentRootPath,
            builder.Configuration["Harness:FrontendPath"]);

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
        builder.Services.AddSingleton<WorkflowTemplateSeeder>();
        builder.Services.AddSingleton<IHostedService, WorkflowTemplateSeedHostedService>();
        builder.Services.AddSingleton<IWorkflowConsistencyReviewer, DeterministicWorkflowConsistencyReviewer>();
        builder.Services.AddSingleton<IRunnerMessageStore>(services =>
            new SqliteRunnerMessageStore(services.GetRequiredService<SqliteWriteDispatcher>()));
        builder.Services.AddSingleton<IOutboxStore, SqliteOutboxStore>();
        builder.Services.AddSingleton<IRealtimeEventStore, SqliteRealtimeEventStore>();
        builder.Services.AddSingleton<IDurableExecutionEngine, SqliteDurableExecutionEngine>();
        builder.Services.AddSingleton<ILocalProfileStore, SqliteLocalProfileStore>();
        builder.Services.AddSingleton<IOrganizationStore, SqliteOrganizationStore>();
        builder.Services.AddSingleton<IProjectStore, SqliteProjectStore>();
        builder.Services.AddSingleton<IAgentCatalogStore, SqliteAgentCatalogStore>();
        builder.Services.AddSingleton<IChiefOrchestratorStore, SqliteChiefOrchestratorStore>();
        builder.Services.AddSingleton<IAgentExecutor, FakeAgentExecutor>();
        builder.Services.AddSingleton<IToolCatalogStore, SqliteToolCatalogStore>();
        builder.Services.AddSingleton<IProviderCatalogStore, SqliteProviderCatalogStore>();
        builder.Services.AddSingleton<INotificationStore, SqliteNotificationStore>();
        builder.Services.AddSingleton<IAuditEventStore, SqliteAuditEventStore>();
        builder.Services.AddSingleton<IPrototypeStore, SqlitePrototypeStore>();
        builder.Services.AddSingleton<IRunTargetStore, SqliteRunTargetStore>();
        builder.Services.AddSingleton<ILicenseStore, SqliteLicenseStore>();
        builder.Services.AddSingleton<ISignedLicenseStore, SqliteSignedLicenseStore>();
        builder.Services.AddSingleton<IChannelLinkStore, SqliteChannelLinkStore>();
        builder.Services.AddSingleton<RunTargetDetector>();
        builder.Services.AddSingleton<RunTargetProcessSupervisor>();
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<RunTargetProcessSupervisor>());
        builder.Services.AddSingleton<ICockpitDigestStore, SqliteCockpitDigestStore>();
        builder.Services.AddSingleton<SqliteConversationStore>();
        builder.Services.AddSingleton<IConversationStore>(services => services.GetRequiredService<SqliteConversationStore>());
        builder.Services.AddSingleton<IChiefTurnStore>(services => services.GetRequiredService<SqliteConversationStore>());
        builder.Services.AddSingleton<IWorkChainStore, SqliteWorkChainStore>();
        builder.Services.AddSingleton<IWorkBoardStore, SqliteWorkBoardStore>();
        builder.Services.AddSingleton<IAttemptWorkspaceStore, SqliteAttemptWorkspaceStore>();
        var isolatedSettings = builder.Configuration
            .GetSection("Harness:IsolatedExecution")
            .Get<IsolatedExecutionSettings>() ?? new IsolatedExecutionSettings();
        builder.Services.AddSingleton(isolatedSettings);
        if (isolatedSettings.Mode != IsolatedExecutionMode.Disabled)
        {
            builder.Services.AddSingleton(isolatedSettings.ToOptions());
            if (isolatedSettings.Mode == IsolatedExecutionMode.Fake)
            {
                builder.Services.AddSingleton<ISandboxProvider, FakeSandboxProvider>();
                builder.Services.AddSingleton<ISandboxAgentExecutorFactory, FakeSandboxAgentExecutorFactory>();
            }
            else
            {
                builder.Services.AddSingleton<ISandboxProvider>(_ => new DockerSandboxProvider());
                builder.Services.AddSingleton<ISandboxAgentExecutorFactory>(services =>
                    new CodexCliSandboxExecutorFactory(
                        services.GetRequiredService<IsolatedExecutionOptions>()));
            }

            builder.Services.AddSingleton(services => new IsolatedAttemptOrchestrator(
                services.GetRequiredService<IAttemptWorkspaceStore>(),
                services.GetRequiredService<ISandboxProvider>(),
                services.GetRequiredService<ISandboxAgentExecutorFactory>(),
                services.GetRequiredService<IClock>(),
                services.GetRequiredService<IsolatedExecutionOptions>()));
        }

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
        builder.Services.AddSingleton(new SolicitationAttachmentStorage(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "attachments")));
        builder.Services.AddSingleton<ISolicitationAttachmentStore, SqliteSolicitationAttachmentStore>();
        builder.Services.AddSingleton<IVisualReferenceAssetStore, SqliteVisualReferenceAssetStore>();
        builder.Services.AddSingleton(services => new LocalOperationsService(
            services.GetRequiredService<SqliteWriteDispatcher>(), databasePath, documentCatalogPath));
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
        builder.Services.AddSingleton(
            new ChiefTurnWorkerOptions(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMinutes(2)));
        builder.Services.AddHostedService<ChiefTurnBackgroundService>();
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
        if (frontendPath is not null)
        {
            var frontendFiles = new PhysicalFileProvider(frontendPath);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFiles });
        }
        app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
            .WithTags("system");
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapHub<EventsHub>("/hubs/events");
        app.MapRunnerIpc();
        app.MapLocalProfiles();
        app.MapOrganizations();
        app.MapProjects();
        app.MapAgents();
        app.MapIsolatedExecutions();
        app.MapToolCatalog();
        app.MapProviders();
        app.MapNotifications();
        app.MapGovernance();
        app.MapPrototypes();
        app.MapVisualReferenceAssets();
        app.MapRunTargets();
        app.MapLicensing();
        app.MapSignedLicenses();
        app.MapConversations();
        app.MapChannels();
        app.MapWorkBoard();
        app.MapSolicitationAttachments();
        app.MapWorkflowCatalog();
        app.MapWorkflowConsistency();
        app.MapDocumentCatalog();
        app.MapLocalOperations();
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

        app.MapFallback((HttpContext context) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
                frontendPath is null)
            {
                return Results.NotFound();
            }
            var indexPath = Path.Combine(frontendPath, "index.html");
            return File.Exists(indexPath)
                ? Results.File(indexPath, "text/html; charset=utf-8")
                : Results.NotFound();
        }).ExcludeFromDescription();

        return app;
    }

    private static string? ResolveFrontendPath(string contentRoot, string? configured)
    {
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(contentRoot, "src", "Harness.Host", "wwwroot"),
            Path.Combine(contentRoot, "wwwroot"),
        };
        AddAncestorCandidates(candidates, contentRoot);
        AddAncestorCandidates(candidates, AppContext.BaseDirectory);
        return candidates.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .FirstOrDefault(value => File.Exists(Path.Combine(value, "index.html")));
    }

    private static void AddAncestorCandidates(List<string?> candidates, string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null;
             directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "src", "Harness.Host", "wwwroot"));
            candidates.Add(Path.Combine(directory.FullName, "wwwroot"));
        }
    }

    private sealed record HealthResponse(string Status);
}
