using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Delivery;
using Harness.Host.Notifications;
using Harness.Host.Organizations;
using Harness.Host.Projects;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Delivery.Application;
using Harness.Modules.Delivery.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Delivery;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.Delivery;

/// <summary>
/// Prova end-to-end (SQLite in-process) da Central de Relatórios: gerar (draft) → aprovar (registra
/// approvedBy) → enviar por um canal FAKE (roteado + auditado, sem vazar segredo); enviar antes de
/// aprovar é 409; o dossiê de encerramento gera com suas seções; formato binário → format_not_available.
/// </summary>
public sealed class DeliveryReportApiTests
{
    [Fact]
    public async Task GenerateApproveAndGatedSendOverHttp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"reports-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "reports.db");
        Directory.CreateDirectory(root);
        var cookies = new CookieContainer();
        try
        {
            await using var app = CreateHost(database);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                var demand = await CreateDemandAsync(client, project.Id, timeout.Token);
                await CreateTaskAsync(client, project.Id, demand.Id, "Implementar serviço", timeout.Token);

                // DEL-04: gerar um status executivo semanal em markdown → rascunho versionado.
                var draft = await GenerateAsync(client, project.Id, "weekly_executive_status", "markdown", timeout.Token);
                Assert.Equal("draft", draft.Status);
                Assert.Equal(1, draft.Version);
                Assert.True(draft.Available);
                Assert.NotNull(draft.Content);
                Assert.NotNull(draft.Document);
                Assert.Null(draft.ApprovedBy);

                // DEL-10: enviar ANTES de aprovar é uma recusa tipada 409 (aprovação humana obrigatória).
                using (var early = await client.PostAsJsonAsync(
                    $"/api/v1/deliveries/{project.Id}/reports/{draft.Id}/send",
                    new SendReportRequest("email", "env://COORD_RECIPIENT"), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
                }

                // DEL-04: aprovação humana — registra approvedBy (o perfil em sessão).
                var approved = await ApproveAsync(client, project.Id, draft.Id, timeout.Token);
                Assert.Equal("approved", approved.Status);
                Assert.Equal("Mateus", approved.ApprovedBy);
                Assert.NotNull(approved.ApprovedAt);

                // DEL-10: com canal AUSENTE por padrão, enviar um relatório aprovado é recusa tipada 409
                // (nada sai por padrão) — não uma exceção.
                using (var noChannel = await client.PostAsJsonAsync(
                    $"/api/v1/deliveries/{project.Id}/reports/{approved.Id}/send",
                    new SendReportRequest("email", "env://COORD_RECIPIENT"), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Conflict, noChannel.StatusCode);
                }

                // Destinatário LITERAL é recusado na fronteira (400) — nunca endereço em claro.
                using (var literal = await client.PostAsJsonAsync(
                    $"/api/v1/deliveries/{project.Id}/reports/{approved.Id}/send",
                    new SendReportRequest("email", "coordination@example.com"), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, literal.StatusCode);
                }

                // Formato binário → snapshot criado, mas conteúdo indisponível (format_not_available).
                var pdf = await GenerateAsync(client, project.Id, "production_readiness", "pdf", timeout.Token);
                Assert.False(pdf.Available);
                Assert.Null(pdf.Content);
                Assert.Equal("format_not_available", pdf.Reason);

                // DEL-05: o dossiê de encerramento gera com suas seções derivadas dos fatos.
                var closure = await GenerateAsync(client, project.Id, "closure_dossier", "json", timeout.Token);
                Assert.NotNull(closure.Document);
                var keys = closure.Document!.Sections.Select(s => s.Key).ToHashSet();
                Assert.Contains("solicited_delivered", keys);
                Assert.Contains("lessons", keys);
                Assert.Contains("residual_risks", keys);

                // Listagem traz os três relatórios (metadados, sem corpo).
                var list = await client.GetFromJsonAsync<DeliveryReportListContract>(
                    $"/api/v1/deliveries/{project.Id}/reports", timeout.Token);
                Assert.NotNull(list);
                Assert.Equal(3, list!.Total);
                Assert.Contains(list.Reports, r => r.Id == approved.Id && r.Status == "approved");
                Assert.Contains(list.Reports, r => r.Type == "closure_dossier");
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ApprovedReportSendsThroughFakeChannelAndIsAuditedWithoutLeakingSecrets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"reports-send-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        const string recipientReference = "env://COORD_RECIPIENT";
        const string resolvedAddress = "coordenacao@poseidon.invalid"; // NUNCA deve aparecer em nada persistido.
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "send.db"), timeout.Token);
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                var now = DateTimeOffset.UtcNow;
                var (tenantId, projectId) = await SeedProjectAsync(dispatcher, now, timeout.Token);

                var store = new SqliteDeliveryReportStore(dispatcher);
                // Semeia um relatório APROVADO diretamente no store (o caminho de composição já é coberto).
                var reportId = UlidValue.New(now.AddMinutes(1)).ToString();
                await store.CreateAsync(new DeliveryReportCreateCommand(
                    tenantId, reportId, projectId, "weekly_executive_status", "markdown", "coordination",
                    "internal", 1, "text/markdown; charset=utf-8", "# Status\n\n- ok\n",
                    "{\"type\":\"weekly_executive_status\",\"title\":\"Status\",\"deliveryId\":\"" + projectId +
                    "\",\"projectKey\":\"PAY\",\"audience\":\"coordination\",\"classification\":\"internal\"," +
                    "\"generatedAt\":\"2026-07-23T12:00:00+00:00\",\"sections\":[]}", now.AddMinutes(1)), timeout.Token);
                Assert.True(await store.ApproveAsync(new DeliveryReportApproveCommand(
                    tenantId, projectId, reportId, "Mateus", now.AddMinutes(2)), timeout.Token));

                var secrets = new FakeSecretResolver(new Dictionary<string, string?> { [recipientReference] = resolvedAddress });
                var channel = new FakeChannel();
                var gateway = new ExternalNotificationGateway(
                    [channel], secrets, NullLogger<ExternalNotificationGateway>.Instance);
                var readModel = app_readModel(dispatcher, now);
                var service = new DeliveryReportService(
                    readModel, store, ReportRendererRegistry.Default(), gateway, new FixedClock(now.AddMinutes(3)));

                var result = await service.SendAsync(
                    tenantId, projectId, reportId, "email", recipientReference, "Mateus", timeout.Token);

                Assert.False(result.IsError);
                Assert.Equal("delivered", result.Value!.Result);
                Assert.Equal(1, result.Value.Version);
                Assert.Equal("sent", result.Value.Report.Status);

                // O canal fake resolveu a referência opaca para o endereço real SÓ no despacho.
                var dispatched = Assert.Single(channel.Sent);
                Assert.Equal(resolvedAddress, dispatched.Recipient);

                // Auditoria registra QUEM/QUANDO/VERSÃO/CANAL e a REFERÊNCIA OPACA — nunca o endereço real.
                var audits = await store.ListSendsAsync(tenantId, reportId, 10, timeout.Token);
                var audit = Assert.Single(audits);
                Assert.Equal("Mateus", audit.SentBy);
                Assert.Equal("email", audit.Channel);
                Assert.Equal(1, audit.Version);
                Assert.Equal(recipientReference, audit.RecipientReference);
                Assert.DoesNotContain(resolvedAddress, audit.RecipientReference);

                // O relatório agora está sent; reenviar é recusa tipada (já enviado).
                var resend = await service.SendAsync(
                    tenantId, projectId, reportId, "email", recipientReference, "Mateus", timeout.Token);
                Assert.True(resend.IsError);
                Assert.Equal(409, resend.Error!.Status);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static DeliveryReadModelService app_readModel(SqliteWriteDispatcher dispatcher, DateTimeOffset now) =>
        new(
            new SqliteProjectStore(dispatcher),
            new SqliteWorkBoardStore(dispatcher),
            new SqliteDocumentCatalogStore(dispatcher),
            new SqliteDeliveryForecastStore(dispatcher),
            new Harness.Modules.Governance.Metrics.SemanticStuckDetector(),
            new FixedClock(now));

    private static async Task<(string TenantId, string ProjectId)> SeedProjectAsync(
        SqliteWriteDispatcher dispatcher, DateTimeOffset now, CancellationToken token)
    {
        var profiles = new SqliteLocalProfileStore(dispatcher);
        var created = await profiles.CreateAsync(new Harness.Persistence.Abstractions.Identity.LocalProfileCreateCommand(
            UlidValue.New(now).ToString(), "Personal", UlidValue.New(now).ToString(), "Mateus", null, null, "pt-BR", now), token);
        var tenantId = created.Profile!.TenantId;
        var organizations = new SqliteOrganizationStore(dispatcher);
        var organizationId = UlidValue.New(now.AddMilliseconds(1)).ToString();
        await organizations.CreateAsync(new Harness.Persistence.Abstractions.Organizations.OrganizationCreateCommand(
            tenantId, organizationId, "Poseidon", "poseidon", "personal",
            new Harness.Persistence.Abstractions.Organizations.OrganizationBrandRecord(null, null, null, null), now), token);
        var projects = new SqliteProjectStore(dispatcher);
        var projectId = UlidValue.New(now.AddMilliseconds(2)).ToString();
        await projects.CreateAsync(new Harness.Persistence.Abstractions.Projects.ProjectCreateCommand(
            tenantId,
            new Harness.Persistence.Abstractions.Projects.ProjectRecord(
                tenantId, projectId, organizationId, "Pagamentos", "PAY", "Backend", "active", "medium", null,
                "local", "main", [], new Harness.Persistence.Abstractions.Projects.ProjectBrandRecord(null, null, null, null),
                [created.Profile.Id], 1, UlidValue.New(now.AddMilliseconds(3)).ToString(), "manual", now, now, 0),
            now.AddMilliseconds(4)), token);
        return (tenantId, projectId);
    }

    private static async Task<DeliveryReportContract> GenerateAsync(
        HttpClient client, string projectId, string type, string format, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/deliveries/{projectId}/reports",
            new GenerateReportRequest(type, format, null, null), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeliveryReportContract>(token))!;
    }

    private static async Task<DeliveryReportContract> ApproveAsync(
        HttpClient client, string projectId, string reportId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/deliveries/{projectId}/reports/{reportId}/approve",
            new ApproveReportRequest(null), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeliveryReportContract>(token))!;
    }

    private static async Task<DemandContract> CreateDemandAsync(
        HttpClient client, string projectId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/demands",
            new CreateDemandRequest(projectId, "Cobrança", "Entregar cobrança.", null, "high"), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DemandContract>(token))!;
    }

    private static async Task CreateTaskAsync(
        HttpClient client, string projectId, string demandId, string title, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/tasks",
            new CreateTaskRequest(projectId, title, "Instrução.", demandId, "high", null, null), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(
        HttpClient client, string organizationId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest
        { OrganizationId = organizationId, Name = "Pagamentos", Key = "PAY", Description = "Backend" }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
    }

    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    private sealed class FakeChannel : IExternalNotificationChannel
    {
        public List<RenderedNotification> Sent { get; } = [];

        public string Channel => "email";

        public bool IsConfigured => true;

        public Task<NotificationDeliveryResult> SendAsync(
            RenderedNotification notification, CancellationToken cancellationToken)
        {
            Sent.Add(notification);
            return Task.FromResult(NotificationDeliveryResult.Ok());
        }
    }

    private sealed class FakeSecretResolver(IReadOnlyDictionary<string, string?> values) : ISecretReferenceResolver
    {
        public string? Resolve(string reference) => values.TryGetValue(reference, out var value) ? value : null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
