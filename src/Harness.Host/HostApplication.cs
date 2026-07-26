using System.Net;
using Harness.Host.Agents;
using Harness.Host.Auth;
using Harness.Host.Conversations;
using Harness.Host.Documents;
using Harness.Host.Demo;
using Harness.Host.Execution;
using Harness.Host.Ipc;
using Harness.Host.Leadership;
using Harness.Host.Licensing;
using Harness.Host.Governance;
using Harness.Host.GovernanceDocs;
using Harness.Host.Organizations;
using Harness.Host.Notifications;
using Harness.Host.Observability;
using Harness.Host.Operations;
using Harness.Host.Persistence;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Providers;
using Harness.Host.Architecture;
using Harness.Host.Delivery;
using Harness.Host.Prototyping;
using Harness.Host.Readiness;
using Harness.Host.Realtime;
using Harness.Host.RunTargets;
using Harness.Host.Security;
using Harness.Host.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Harness.Host.WorkBoard;
using Harness.Host.Workflows;
using Harness.Host.Tools;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.Conversation;
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.Modules.Agents.Infrastructure.OmpRpc;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Execution.Infrastructure.Sandbox;
using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Evaluation;
using Harness.Modules.Governance.Documentation;
using Harness.Modules.Governance.Judging;
using Harness.Modules.Governance.Metrics;
using Harness.Modules.Governance.Patching;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Architecture;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Cockpit;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Delivery;
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
using Harness.Persistence.Postgres;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.FileProviders;
using Npgsql;

namespace Harness.Host;

public static class HostApplication
{
    public static WebApplication Build(
        string[] args,
        RunnerIpcToken? runnerIpcToken = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddPoseidonTelemetry(builder.Configuration);

        var frontendPath = ResolveFrontendPath(builder.Environment.ContentRootPath,
            builder.Configuration["Harness:FrontendPath"]);

        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<LeadershipProfileStore>();
        builder.Services.AddSingleton(runnerIpcToken ?? RunnerIpcToken.Create());
        var databasePath = builder.Configuration["Harness:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(AppContext.BaseDirectory, "data", "harness.db");
        }

        var databaseProvider = (builder.Configuration["Harness:Database:Provider"] ?? "sqlite")
            .ToLowerInvariant();
        var serverMode = databaseProvider == "postgres";
        if (serverMode)
        {
            var connectionString = builder.Configuration["Harness:Database:ConnectionString"];
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Harness:Database:ConnectionString é obrigatório quando Harness:Database:Provider=postgres.");
            }

            builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
            builder.Services.AddSingleton<IHostedService, PostgresMigrationHostedService>();
        }
        else
        {
            builder.Services.AddSingleton(
                _ => SqliteWriteDispatcher.CreateAsync(databasePath).GetAwaiter().GetResult());
            builder.Services.AddSingleton<IHostedService, SqliteMigrationHostedService>();
        }

        var oidc = OidcSettings.From(builder.Configuration);
        var serverOptions = new HarnessServerOptions(
            Multiuser: serverMode || oidc.Enabled,
            RateLimitPermitsPerMinute: serverMode
                ? int.TryParse(
                    builder.Configuration["Harness:Server:RateLimitPermitsPerMinute"],
                    out var permits) && permits > 0 ? permits : 600
                : 0);
        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton(
            builder.Configuration.GetSection("Harness:Security:Headers").Get<SecurityHeadersOptions>()
            ?? new SecurityHeadersOptions());
        if (oidc.Enabled)
        {
            builder.Services.AddSingleton(oidc);
            builder.Services.AddSingleton<OidcProfileProvisioner>();
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = oidc.Authority;
                    options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters.ValidAudience = oidc.Audience;
                    options.Events = new JwtBearerEvents
                    {
                        // SignalR entrega o token pela query string no hub de eventos.
                        OnMessageReceived = context =>
                        {
                            if (context.HttpContext.Request.Path.StartsWithSegments(
                                    "/hubs", StringComparison.Ordinal) &&
                                context.Request.Query.TryGetValue("access_token", out var token))
                            {
                                context.Token = token;
                            }

                            return Task.CompletedTask;
                        },
                    };
                });
        }

        if (serverOptions.RateLimitPermitsPerMinute > 0)
        {
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter
                    .Create<HttpContext, string>(context =>
                        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                            context.Connection.RemoteIpAddress?.ToString() ?? "local",
                            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                            {
                                PermitLimit = serverOptions.RateLimitPermitsPerMinute,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                            }));
            });
        }
        builder.Services.AddSingleton<WorkflowTemplateSeeder>();
        builder.Services.AddSingleton<IHostedService, WorkflowTemplateSeedHostedService>();
        builder.Services.AddSingleton<BuiltInAgentDefinitionSeeder>();
        builder.Services.AddSingleton<IHostedService, BuiltInAgentDefinitionSeedHostedService>();
        builder.Services.AddSingleton<IWorkflowConsistencyReviewer, DeterministicWorkflowConsistencyReviewer>();
        // O executor simulado só participa sob configuração explícita de demonstração ou
        // desenvolvimento (ADR-019). No pacote de homologação normal não há executor simulado:
        // sem executor real o turno falha de forma honesta em vez de devolver texto fabricado.
        var simulatedExecutor =
            builder.Configuration.GetValue<bool>("Harness:Demo:Enabled") ||
            string.Equals(
                builder.Configuration["Harness:AgentExecutors:Mode"],
                "simulated",
                StringComparison.OrdinalIgnoreCase);
        if (simulatedExecutor)
        {
            builder.Services.AddSingleton<IAgentExecutor>(
                _ => new InstrumentedAgentExecutor(new FakeAgentExecutor()));
        }
        else
        {
            builder.Services.AddSingleton<IAgentExecutor>(
                _ => new InstrumentedAgentExecutor(new UnavailableAgentExecutor()));
        }
        if (serverMode)
        {
            builder.Services.AddSingleton<IRunnerMessageStore, PostgresRunnerMessageStore>();
            builder.Services.AddSingleton<IOutboxStore, PostgresOutboxStore>();
            builder.Services.AddSingleton<IRealtimeEventStore, PostgresRealtimeEventStore>();
            builder.Services.AddSingleton<PostgresDurableExecutionEngine>();
            builder.Services.AddSingleton<IDurableExecutionEngine>(services =>
                new InstrumentedDurableExecutionEngine(
                    services.GetRequiredService<PostgresDurableExecutionEngine>()));
            builder.Services.AddSingleton<ILocalProfileStore, PostgresLocalProfileStore>();
            builder.Services.AddSingleton<IOrganizationStore, PostgresOrganizationStore>();
            builder.Services.AddSingleton<IProjectStore, PostgresProjectStore>();
            builder.Services.AddSingleton<IAgentCatalogStore, PostgresAgentCatalogStore>();
            builder.Services.AddSingleton<ITeamSpecialtyCatalogStore, PostgresTeamSpecialtyCatalogStore>();
            builder.Services.AddSingleton<IChiefOrchestratorStore, PostgresChiefOrchestratorStore>();
            builder.Services.AddSingleton<IToolCatalogStore, PostgresToolCatalogStore>();
            builder.Services.AddSingleton<IProviderCatalogStore, PostgresProviderCatalogStore>();
            builder.Services.AddSingleton<INotificationStore, PostgresNotificationStore>();
            builder.Services.AddSingleton<IAuditEventStore, PostgresAuditEventStore>();
            builder.Services.AddSingleton<IGovernanceRuntimeStore, PostgresGovernanceRuntimeStore>();
            builder.Services.AddSingleton<ILearningCandidateStore, PostgresLearningCandidateStore>();
            builder.Services.AddSingleton<IPrototypeStore, PostgresPrototypeStore>();
            builder.Services.AddSingleton<IRunTargetStore, PostgresRunTargetStore>();
            builder.Services.AddSingleton<ILicenseStore, PostgresLicenseStore>();
            builder.Services.AddSingleton<ISignedLicenseStore, PostgresSignedLicenseStore>();
            builder.Services.AddSingleton<IChannelLinkStore, PostgresChannelLinkStore>();
            builder.Services.AddSingleton<Harness.SharedKernel.Memory.IVectorIndex, PostgresVectorIndex>();
        }
        else
        {
            builder.Services.AddSingleton<IRunnerMessageStore>(services =>
                new SqliteRunnerMessageStore(services.GetRequiredService<SqliteWriteDispatcher>()));
            builder.Services.AddSingleton<IOutboxStore, SqliteOutboxStore>();
            builder.Services.AddSingleton<IRealtimeEventStore, SqliteRealtimeEventStore>();
            builder.Services.AddSingleton<SqliteDurableExecutionEngine>();
            builder.Services.AddSingleton<IDurableExecutionEngine>(services =>
                new InstrumentedDurableExecutionEngine(
                    services.GetRequiredService<SqliteDurableExecutionEngine>()));
            builder.Services.AddSingleton<ILocalProfileStore, SqliteLocalProfileStore>();
            builder.Services.AddSingleton<IOrganizationStore, SqliteOrganizationStore>();
            builder.Services.AddSingleton<IProjectStore, SqliteProjectStore>();
            builder.Services.AddSingleton<IAgentCatalogStore, SqliteAgentCatalogStore>();
            builder.Services.AddSingleton<ITeamSpecialtyCatalogStore, SqliteTeamSpecialtyCatalogStore>();
            builder.Services.AddSingleton<IChiefOrchestratorStore, SqliteChiefOrchestratorStore>();
            builder.Services.AddSingleton<IToolCatalogStore, SqliteToolCatalogStore>();
            builder.Services.AddSingleton<IProviderCatalogStore, SqliteProviderCatalogStore>();
            builder.Services.AddSingleton<INotificationStore, SqliteNotificationStore>();
            builder.Services.AddSingleton<IAuditEventStore, SqliteAuditEventStore>();
            builder.Services.AddSingleton<IGovernanceRuntimeStore, SqliteGovernanceRuntimeStore>();
            builder.Services.AddSingleton<ILearningCandidateStore, SqliteLearningCandidateStore>();
            builder.Services.AddSingleton<IPrototypeStore, SqlitePrototypeStore>();
            builder.Services.AddSingleton<IRunTargetStore, SqliteRunTargetStore>();
            builder.Services.AddSingleton<ILicenseStore, SqliteLicenseStore>();
            builder.Services.AddSingleton<ISignedLicenseStore, SqliteSignedLicenseStore>();
            builder.Services.AddSingleton<IChannelLinkStore, SqliteChannelLinkStore>();
        }
        // Notification Router do contrato de canais: elege o último canal ativo por conversa.
        builder.Services.AddSingleton<ActiveChannelRouter>();
        // Output Gateway: única porta de saída externa, em nome da Bruna e auditável.
        builder.Services.AddSingleton<ChannelOutputGateway>();
        builder.Services.AddSingleton(builder.Configuration
            .GetSection("Harness:Channels:Telegram")
            .Get<TelegramChannelOptions>() ?? new TelegramChannelOptions());
        builder.Services.AddHostedService<TelegramChannelBackgroundService>();
        builder.Services.AddSingleton(builder.Configuration
            .GetSection("Harness:Channels:Teams")
            .Get<TeamsChannelOptions>() ?? new TeamsChannelOptions());
        builder.Services.AddSingleton<TeamsChannelBackgroundService>();
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<TeamsChannelBackgroundService>());
        builder.Services.AddSingleton(builder.Configuration
            .GetSection("Harness:Channels:WhatsApp")
            .Get<WhatsAppChannelOptions>() ?? new WhatsAppChannelOptions());
        builder.Services.AddSingleton<WhatsAppChannelBackgroundService>();
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<WhatsAppChannelBackgroundService>());
        RegisterSmtpNotificationChannel(builder);
        RegisterCapabilityEnforcement(builder);
        // O canal conversacional de e-mail reaproveita o relay SMTP registrado acima:
        // duas fontes de verdade para o mesmo relay seriam defeito de canon.
        builder.Services.AddSingleton(builder.Configuration
            .GetSection("Harness:Channels:Email")
            .Get<EmailChannelOptions>() ?? new EmailChannelOptions());
        builder.Services.AddSingleton<EmailChannelBackgroundService>();
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<EmailChannelBackgroundService>());
        builder.Services.AddSingleton<RunTargetAgentFallback>();
        builder.Services.AddSingleton<RunTargetDetector>();
        builder.Services.AddSingleton<DockerRunTargetLifecycle>();
        builder.Services.AddSingleton<RunTargetProcessSupervisor>();
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<RunTargetProcessSupervisor>());
        if (serverMode)
        {
            builder.Services.AddSingleton<ICockpitDigestStore, PostgresCockpitDigestStore>();
            builder.Services.AddSingleton<PostgresConversationStore>();
            builder.Services.AddSingleton<IConversationStore>(services => services.GetRequiredService<PostgresConversationStore>());
            builder.Services.AddSingleton<IChiefTurnStore>(services => services.GetRequiredService<PostgresConversationStore>());
            builder.Services.AddSingleton<IChiefContextNoteStore, PostgresChiefContextNoteStore>();
            builder.Services.AddSingleton<IWorkChainStore, PostgresWorkChainStore>();
            builder.Services.AddSingleton<IWorkBoardStore, PostgresWorkBoardStore>();
            builder.Services.AddSingleton<IDemandPlanStore, PostgresDemandPlanStore>();
            builder.Services.AddSingleton<IDeliveryForecastStore, PostgresDeliveryForecastStore>();
            builder.Services.AddSingleton<IDeliveryReportStore, PostgresDeliveryReportStore>();
            builder.Services.AddSingleton<IDeliveryDailyStore, PostgresDeliveryDailyStore>();
            builder.Services.AddSingleton<IArchitectureStore, PostgresArchitectureStore>();
            builder.Services.AddSingleton<IAttemptWorkspaceStore, PostgresAttemptWorkspaceStore>();
        }
        else
        {
            builder.Services.AddSingleton<ICockpitDigestStore, SqliteCockpitDigestStore>();
            builder.Services.AddSingleton<SqliteConversationStore>();
            builder.Services.AddSingleton<IConversationStore>(services => services.GetRequiredService<SqliteConversationStore>());
            builder.Services.AddSingleton<IChiefTurnStore>(services => services.GetRequiredService<SqliteConversationStore>());
            builder.Services.AddSingleton<IChiefContextNoteStore, SqliteChiefContextNoteStore>();
            builder.Services.AddSingleton<IWorkChainStore, SqliteWorkChainStore>();
            builder.Services.AddSingleton<IWorkBoardStore, SqliteWorkBoardStore>();
            builder.Services.AddSingleton<IDemandPlanStore, SqliteDemandPlanStore>();
            builder.Services.AddSingleton<IDeliveryForecastStore, SqliteDeliveryForecastStore>();
            builder.Services.AddSingleton<IDeliveryReportStore, SqliteDeliveryReportStore>();
            builder.Services.AddSingleton<IDeliveryDailyStore, SqliteDeliveryDailyStore>();
            builder.Services.AddSingleton<IArchitectureStore, SqliteArchitectureStore>();
            builder.Services.AddSingleton<IAttemptWorkspaceStore, SqliteAttemptWorkspaceStore>();
            builder.Services.AddSingleton<Harness.SharedKernel.Memory.IVectorIndex, SqliteVectorIndex>();
        }
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
                if (string.Equals(isolatedSettings.ExecutorId, "omp-rpc", StringComparison.Ordinal))
                {
                    builder.Services.AddSingleton<ISandboxAgentExecutorFactory>(services =>
                        new OmpRpcSandboxExecutorFactory(
                            services.GetRequiredService<OmpRpcAgentExecutorOptions>()));
                }
                else
                {
                    builder.Services.AddSingleton<ISandboxAgentExecutorFactory>(services =>
                        new CodexCliSandboxExecutorFactory(
                            services.GetRequiredService<IsolatedExecutionOptions>()));
                }
            }

            builder.Services.AddSingleton(services => new IsolatedAttemptOrchestrator(
                services.GetRequiredService<IAttemptWorkspaceStore>(),
                services.GetRequiredService<ISandboxProvider>(),
                services.GetRequiredService<ISandboxAgentExecutorFactory>(),
                services.GetRequiredService<IClock>(),
                services.GetRequiredService<IsolatedExecutionOptions>()));
        }

        // Bootstrap governado de agentes externos (CA-5). As settings são sempre
        // registradas para que o endpoint saiba responder `agent_runs_disabled`; o
        // orquestrador só existe quando o recurso está habilitado e configurado.
        var agentRunSettings = builder.Configuration
            .GetSection("Harness:AgentRuns")
            .Get<AgentRunSettings>() ?? new AgentRunSettings();
        builder.Services.AddSingleton(agentRunSettings);

        // RN-02: com a execução de agentes ligada, garante a invariante "todo projeto tem workflow"
        // — vinculando o recomendado a projetos sem workflow no startup. Gate PURO em AgentRuns.Enabled
        // (não exige conta do Chefe nem raiz controlada), para que projetos antigos (ex.: "Poseidon")
        // ganhem fase/workflow e o chat do Chefe nunca fique em "Nenhum workflow ativo".
        if (agentRunSettings.Enabled)
        {
            builder.Services.AddSingleton<Workflows.ProjectWorkflowConvergenceSeeder>();
            builder.Services.AddHostedService<Workflows.ProjectWorkflowConvergenceHostedService>();

            // RN-03: logo após garantir o workflow, converge o ESTADO REAL do projeto Poseidon —
            // inicia a run do binding (fase/em andamento), registra o front como protótipo e preenche
            // a marca da organização quando vazia. Idempotente e honesto (nunca fabrica progresso).
            // Registrado DEPOIS da convergência de workflow para que o binding já exista quando roda.
            builder.Services.AddSingleton<Projects.ProjectStateConvergenceSeeder>();
            builder.Services.AddHostedService<Projects.ProjectStateConvergenceHostedService>();

            // UX-PROTO/ARCH: com a execução de agentes ligada, semeia (idempotente) o MAPA DE
            // ARQUITETURA do próprio Poseidon — elementos + relacionamentos derivados da estrutura
            // REAL do repositório (src/, src/Modules/, frontend/) — para que a tela /architecture
            // deixe de nascer vazia e o dono abra o projeto e veja o produto já mapeado.
            builder.Services.AddSingleton<Architecture.ArchitectureSelfMapSeeder>();
            builder.Services.AddHostedService<Architecture.ArchitectureSelfMapSeedHostedService>();
        }

        // O roster público existe também quando a execução externa está desabilitada. O ledger
        // é somente leitura nesse cenário e permite expor o último estado observado de forma
        // honesta, sem transformar a dependência do endpoint em um body inferido.
        builder.Services.AddSingleton(
            new AccountAvailabilityLedger(AccountAvailabilityLedger.DefaultPath));

        if (agentRunSettings.Enabled && !string.IsNullOrWhiteSpace(agentRunSettings.ControlledRoot))
        {
            var profilesRoot = string.IsNullOrWhiteSpace(agentRunSettings.ProfilesRoot)
                ? AccountProfileProvisioner.DefaultProfilesRoot
                : Path.GetFullPath(agentRunSettings.ProfilesRoot);
            builder.Services.AddSingleton(new AccountProfileProvisioner(
                profilesRoot,
                [Path.GetFullPath(agentRunSettings.ControlledRoot)]));
            builder.Services.AddSingleton(
                AgentAccountConfigurationLoader.Load(agentRunSettings.AccountsFilePath));
            builder.Services.AddHostedService<AccountRecoveryBackgroundService>();
            builder.Services.AddSingleton(new ChiefBacklogPolicy());
            builder.Services.AddHostedService<ChiefBacklogLoopService>();
            builder.Services.AddSingleton(new AttemptArtifactArchive(
                string.IsNullOrWhiteSpace(agentRunSettings.ArchiveRoot)
                    ? AttemptArtifactArchive.DefaultRoot
                    : Path.GetFullPath(agentRunSettings.ArchiveRoot)));
            builder.Services.AddSingleton(services => new ExternalAgentExecutorFactory(
                services.GetRequiredService<AccountProfileProvisioner>()));

            // GP-06: com AgentRuns habilitado e raiz controlada declarada, o turno de conversa
            // do Chefe passa a ser executado DE VERDADE pela CLI (assinatura Claude Code da
            // conta chief-orchestrator), no caminho LEVE (sem worktree/claim). Esta é a última
            // registração de IAgentExecutor, então vence o UnavailableAgentExecutor default. O
            // próprio executor cai para falha honesta (AgentExecutorUnavailableException,
            // idêntica ao Unavailable) quando a conta do Chefe está ausente ou desabilitada.
            builder.Services.AddSingleton<IAgentExecutor>(services =>
                new InstrumentedAgentExecutor(
                    new ConversationChiefAgentExecutor(
                        services.GetRequiredService<AgentAccountRegistry>(),
                        services.GetRequiredService<AccountProfileProvisioner>(),
                        executorId => services
                            .GetRequiredService<ExternalAgentExecutorFactory>()
                            .Create(executorId),
                        services.GetRequiredService<IClock>(),
                        new ConversationChiefExecutorOptions(
                            Path.GetFullPath(agentRunSettings.ControlledRoot!)))));

            builder.Services.AddSingleton(services => new AgentRunOrchestrator(
                services.GetRequiredService<IAttemptWorkspaceStore>(),
                services.GetRequiredService<IGovernanceRuntimeStore>(),
                services.GetRequiredService<ContextBundleBuilder>(),
                services.GetRequiredService<AccountProfileProvisioner>(),
                services.GetRequiredService<AgentAccountRegistry>(),
                services.GetRequiredService<ExternalAgentExecutorFactory>(),
                services.GetRequiredService<EventPublisher>(),
                services.GetRequiredService<IClock>(),
                services.GetRequiredService<AgentRunSettings>(),
                services.GetRequiredService<AccountAvailabilityLedger>()));

            // GP-06 (fecho): com o Chefe executável pela CLI, semeia de forma idempotente a conta e
            // o modelo REAIS que o gate de prontidão e o roteamento exigem, aponta o chefe para
            // eles e vincula o workflow recomendado. Sobrevive a restart: reexecuta e converge.
            builder.Services.AddSingleton<Providers.ChiefCliProviderCatalogSeeder>();
            builder.Services.AddHostedService<Providers.ChiefCliProviderCatalogSeedHostedService>();
        }

        if (serverMode)
        {
            builder.Services.AddSingleton<IWorkflowStore, PostgresWorkflowStore>();
            builder.Services.AddSingleton<IWorkflowCatalogStore, PostgresWorkflowCatalogStore>();
            builder.Services.AddSingleton<IDocumentStore, PostgresDocumentStore>();
            builder.Services.AddSingleton<IDocumentCatalogStore, PostgresDocumentCatalogStore>();
        }
        else
        {
            builder.Services.AddSingleton<IWorkflowStore, SqliteWorkflowStore>();
            builder.Services.AddSingleton<IWorkflowCatalogStore, SqliteWorkflowCatalogStore>();
            builder.Services.AddSingleton<IDocumentStore, SqliteDocumentStore>();
            builder.Services.AddSingleton<IDocumentCatalogStore, SqliteDocumentCatalogStore>();
        }
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
        if (serverMode)
        {
            builder.Services.AddSingleton<ISolicitationAttachmentStore, PostgresSolicitationAttachmentStore>();
            builder.Services.AddSingleton<IVisualReferenceAssetStore, PostgresVisualReferenceAssetStore>();
        }
        else
        {
            builder.Services.AddSingleton<ISolicitationAttachmentStore, SqliteSolicitationAttachmentStore>();
            builder.Services.AddSingleton<IVisualReferenceAssetStore, SqliteVisualReferenceAssetStore>();
            builder.Services.AddSingleton(services => new LocalOperationsService(
                services.GetRequiredService<SqliteWriteDispatcher>(), databasePath, documentCatalogPath));
        }
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
            new WorkBoard.BoardStateReconciliationOptions(
                TimeSpan.FromMinutes(2),
                250));
        builder.Services.AddHostedService<WorkBoard.BoardStateReconciliationBackgroundService>();
        var governanceFeatures = builder.Configuration
            .GetSection("Harness:Governance:Features")
            .Get<GovernanceFeatureSettings>() ?? new GovernanceFeatureSettings();
        builder.Services.AddSingleton(governanceFeatures);
        builder.Services.AddSingleton(
            new ChiefTurnWorkerOptions(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMinutes(2))
            {
                ContextBundlesEnabled = governanceFeatures.ContextBundlesEnabled,
                ContextTokenBudget = governanceFeatures.ContextTokenBudget,
            });
        // PLAT-02: estratégia de contexto do Chief registrada atrás da interface (trocável por
        // deployment). O orçamento/limiar vem das settings Harness:Governance:Features com defaults
        // seguros; a estratégia só é aplicada na montagem do turno quando explicitamente ligada.
        builder.Services.AddSingleton<IContextStrategy, DefaultContextStrategy>();
        builder.Services.AddSingleton(new ChiefContextStrategyOptions(
            governanceFeatures.ChiefContextStrategyEnabled,
            new ContextStrategyBudget(
                governanceFeatures.ChiefContextMaxTokens,
                governanceFeatures.ChiefContextRecentTurns,
                governanceFeatures.ChiefContextToolResultWindow),
            governanceFeatures.ChiefContextHistoryScanLimit));
        builder.Services.AddSingleton(services => new ChiefContextComposer(
            services.GetRequiredService<IConversationStore>(),
            services.GetRequiredService<IContextStrategy>(),
            services.GetRequiredService<IChiefContextNoteStore>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<ChiefContextStrategyOptions>()));
        var governanceRoot = ResolveGovernanceRoot(builder.Environment.ContentRootPath)
            ?? throw new DirectoryNotFoundException("governance/manifest.yaml is required by the Chief runtime.");
        // Raiz do repositório para leitura/escrita dos docs de governança pela UI.
        // Prioriza config explícita; senão usa a raiz canônica da governança (mesma
        // que o runtime lê do disco, logo edições passam a valer para o Chefe).
        var governanceDocsRoot = builder.Configuration["Harness:GovernanceDocsRoot"];
        builder.Services.AddSingleton(new GovernanceDocsService(
            string.IsNullOrWhiteSpace(governanceDocsRoot) ? governanceRoot : governanceDocsRoot));
        builder.Services.AddSingleton(new ContextBundleBuilder(governanceRoot));
        builder.Services.AddSingleton(new StaleDocumentDetector(
            governanceRoot,
            new StaleDocumentDetectorOptions { Enabled = governanceFeatures.StaleDocumentDetectorEnabled }));
        builder.Services.AddSingleton(new HashlinePatchOptions
        {
            Enabled = governanceFeatures.HashlinePatchesEnabled,
        });
        var evaluatorOptions = builder.Configuration
            .GetSection("Harness:Governance:Evaluator")
            .Get<FreshContextEvaluatorOptions>() ?? new FreshContextEvaluatorOptions();
        builder.Services.AddSingleton(evaluatorOptions);
        builder.Services.AddSingleton<IFreshContextEvaluator, FreshContextEvaluator>();
        builder.Services.AddSingleton<Harness.Modules.Governance.Evaluation.IEvaluationService, Harness.Modules.Governance.Evaluation.EvaluationService>();
        builder.Services.AddSingleton<Harness.Modules.Providers.Application.CapacityManager>();
        builder.Services.AddSingleton<Harness.Modules.Providers.Application.ModelRouter>();
        builder.Services.AddSingleton<Harness.Modules.Governance.Memory.IHybridRagSearchEngine, Harness.Modules.Governance.Memory.HybridRagSearchEngine>();
        builder.Services.AddSingleton<Harness.Modules.Governance.Memory.IContextBuilder, Harness.Modules.Governance.Memory.ContextBuilder>();
        builder.Services.AddSingleton<Harness.Modules.Coordination.Application.IMultimodalIntakeService, Harness.Modules.Coordination.Application.MultimodalIntakeService>();
        builder.Services.AddSingleton<Harness.Modules.Governance.Ledger.ILedgerReconciliationService, Harness.Modules.Governance.Ledger.LedgerReconciliationService>();
        // PLAT-04: camada de medição. O detector de travamento é PURO (sempre disponível, read-only).
        // O juiz default é determinístico e sem credenciais; o juiz real ligado a um LLM só entra
        // quando explicitamente habilitado E com transport configurado (default: DESLIGADO).
        var stuckOptions = builder.Configuration
            .GetSection("Harness:Governance:StuckDetector")
            .Get<StuckDetectorOptions>() ?? new StuckDetectorOptions();
        builder.Services.AddSingleton(stuckOptions);
        builder.Services.AddSingleton<SemanticStuckDetector>();
        // Central de Entregas (DEL-01/02/09): read-model sobre Projects/Coordination/Documents/PLAT-04.
        builder.Services.AddSingleton<Harness.Host.Delivery.DeliveryReadModelService>();
        // Central de Relatórios (DEL-04/05/10): renderizadores reais (BCL) + serviço de relatórios.
        builder.Services.AddSingleton(Harness.Modules.Delivery.Application.ReportRendererRegistry.Default());
        builder.Services.AddSingleton<Harness.Host.Delivery.DeliveryReportService>();
        // CAT-07: read-model de atividade recente por projeto (sobre work board + previsões).
        builder.Services.AddSingleton<Harness.Host.Projects.ProjectActivityReadModelService>();
        // Architecture Hub (ARC-01/02/03/05): read-model + comandos sobre o modelo arquitetural estruturado.
        builder.Services.AddSingleton<Harness.Host.Architecture.ArchitectureReadModelService>();
        builder.Services.AddSingleton<Harness.Host.Architecture.ArchitectureCommandService>();
        // Architecture Hub estendido (ARC-06/07/08/10): descoberta, insights, padrões e baseline da entrega.
        builder.Services.AddSingleton<Harness.Host.Architecture.ArchitectureHubCommandService>();
        var evalJudgeOptions = builder.Configuration
            .GetSection("Harness:Governance:EvalJudge")
            .Get<EvalJudgeOptions>() ?? new EvalJudgeOptions();
        builder.Services.AddSingleton(evalJudgeOptions);
        builder.Services.AddSingleton(services => new EvalJudgeFactory(
            services.GetRequiredService<EvalJudgeOptions>()));
        builder.Services.AddSingleton<IEvalJudge>(services =>
            services.GetRequiredService<EvalJudgeFactory>().Create());
        var ompOptions = builder.Configuration
            .GetSection("Harness:AgentExecutors:OmpRpc")
            .Get<OmpRpcAgentExecutorOptions>() ?? new OmpRpcAgentExecutorOptions();
        builder.Services.AddSingleton(ompOptions);
        builder.Services.AddSingleton<AgentExecutorCatalog>();
        builder.Services.AddSingleton<ChiefInvocationRoutingService>();
        builder.Services.AddSingleton<Readiness.ProjectReadinessService>();
        builder.Services.AddHostedService<ChiefTurnBackgroundService>();
        if (builder.Configuration.GetValue<bool>("Harness:Demo:Enabled"))
        {
            builder.Services.AddHostedService<DemoDataHostedService>();
        }
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
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<EndpointTelemetryMiddleware>();
        if (frontendPath is not null)
        {
            var frontendFiles = new PhysicalFileProvider(frontendPath);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFiles });
        }
        app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
            .WithTags("system");

        app.MapGet("/ready", () => Results.Ok(new Dictionary<string, string>
        {
            { "status", "ready" },
            { "database", "connected" },
            { "mode", "server" }
        })).WithTags("system");

        app.MapGet("/metrics", () =>
        {
            var uptime = (DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds;
            var prometheusText = $"""
                # HELP poseidon_uptime_seconds System uptime in seconds
                # TYPE poseidon_uptime_seconds gauge
                poseidon_uptime_seconds {uptime:F2}
                # HELP poseidon_health_status System health status (1 = healthy)
                # TYPE poseidon_health_status gauge
                poseidon_health_status 1
                """;
            return Results.Text(prometheusText, "text/plain; version=0.0.4");
        }).WithTags("system");
        var frontendAssets = frontendPath is null ? null : Path.Combine(frontendPath, "assets");
        var fallbackFavicon = frontendAssets is null || !Directory.Exists(frontendAssets)
            ? null
            : Directory.EnumerateFiles(
                    frontendAssets,
                    "logo-icon-*.png",
                    SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        if (fallbackFavicon is not null)
        {
            app.MapGet("/favicon.ico", () => Results.File(fallbackFavicon, "image/png"))
                .ExcludeFromDescription();
        }
        app.MapOpenApi("/openapi/{documentName}.json");
        if (oidc.Enabled)
        {
            app.UseAuthentication();
            app.UseMiddleware<OidcSessionMiddleware>();
        }

        // Modo pessoal (não multiusuário/OIDC): adota o único perfil local como sessão,
        // para que um navegador novo (sem cookie) não fique travado sem login.
        if (!serverOptions.Multiuser)
        {
            app.UseMiddleware<PersonalProfileSessionMiddleware>();
        }

        if (serverOptions.RateLimitPermitsPerMinute > 0)
        {
            app.UseRateLimiter();
        }

        app.MapHub<EventsHub>("/hubs/events");
        app.MapRunnerIpc();
        app.MapLocalProfiles();
        app.MapLeadershipProfile();
        app.MapOrganizations();
        app.MapProjects();
        app.MapProjectActivity();
        app.MapReadiness();
        app.MapAgents();
        app.MapTeamSpecialtyCatalog();
        app.MapIsolatedExecutions();
        app.MapAgentRuns();
        app.MapToolCatalog();
        app.MapProviders();
        app.MapNotifications();
        app.MapGovernance();
        app.MapGovernanceRuntime();
        app.MapGovernanceDocs();
        app.MapLearningCandidates();
        app.MapPrototypes();
        app.MapVisualReferenceAssets();
        app.MapRunTargets();
        app.MapLicensing();
        app.MapSignedLicenses();
        app.MapConversations();
        app.MapChannels();
        app.MapWorkBoard();
        app.MapBacklogHealth();
        app.MapDemandPlans();
        app.MapDeliveries();
        app.MapArchitecture();
        app.MapArchitectureHub();
        app.MapSolicitationAttachments();
        app.MapWorkflowCatalog();
        app.MapWorkflowConsistency();
        app.MapDocumentCatalog();
        if (serverMode)
        {
            app.MapServerOperationsUnavailable();
        }
        else
        {
            app.MapLocalOperations();
        }
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

    /// <summary>
    /// Registra o canal de e-mail SMTP e o gateway de notificações externas. O canal só é
    /// injetado como <see cref="IExternalNotificationChannel"/> quando o deploy o configurou
    /// (<c>Harness:Notifications:Smtp</c> com Enabled + referências opacas); sem isso, o
    /// gateway existe mas roteia para nada, preservando o comportamento padrão (canal OFF).
    /// </summary>
    private static void RegisterSmtpNotificationChannel(WebApplicationBuilder builder)
    {
        var options = builder.Configuration
            .GetSection("Harness:Notifications:Smtp")
            .Get<SmtpNotificationOptions>() ?? new SmtpNotificationOptions();
        // Recusa referência literal já na composição: falha rápido em deploy mal configurado,
        // sem nunca aceitar um segredo embutido.
        SmtpNotificationOptionsValidator.EnsureOpaqueReferences(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISecretReferenceResolver, EnvironmentSecretReferenceResolver>();
        builder.Services.AddSingleton<ISmtpTransport, SystemNetSmtpTransport>();
        if (options.IsConfigured)
        {
            builder.Services.AddSingleton<IExternalNotificationChannel, SmtpNotificationChannel>();
        }

        builder.Services.AddSingleton<ExternalNotificationGateway>();
    }

    /// <summary>
    /// Liga o PEP de capability ao ledger append-only. O ponto de decisão já existia como
    /// componente do módulo Tools, mas sem sink de auditoria de produção: as decisões viviam só em
    /// memória. Com o <see cref="LedgerSecurityAuditor"/> registrado, toda autorização e toda
    /// negativa de capability passam a ser fato auditável (`governance/rules/security.md`).
    /// </summary>
    private static void RegisterCapabilityEnforcement(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<LedgerSecurityAuditor>();
        builder.Services.AddSingleton<Harness.Modules.Tools.Application.ICapabilityDecisionAuditSink>(
            services => services.GetRequiredService<LedgerSecurityAuditor>());
        builder.Services.AddSingleton<Harness.Modules.Tools.Application.SecurityPolicyEnforcementPoint>();
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

    private static string? ResolveGovernanceRoot(string contentRoot)
    {
        // O diretório de instalação (onde vivem os binários) é a fonte de verdade da governança
        // empacotada. Resolvê-lo a partir de AppContext.BaseDirectory ANTES do ContentRoot/CWD
        // impede que um pacote instalado dependa acidentalmente de um checkout de desenvolvimento
        // presente no diretório atual — comportamento não determinístico e proibido em produção.
        foreach (var start in new[] { AppContext.BaseDirectory, contentRoot })
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "governance", "manifest.yaml")))
                {
                    return directory.FullName;
                }
            }
        }

        return null;
    }

    private sealed record HealthResponse(string Status);
}

public sealed record HarnessServerOptions(bool Multiuser, int RateLimitPermitsPerMinute);
