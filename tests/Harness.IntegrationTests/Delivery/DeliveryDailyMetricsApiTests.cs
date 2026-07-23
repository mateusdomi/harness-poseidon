using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Projects;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Delivery.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Delivery;

/// <summary>
/// Prova end-to-end (SQLite in-process) do Daily Copilot (DEL-03) e das Métricas (DEL-06): o briefing
/// deriva dados coerentes, a captura persiste de forma durável, o resumo agrupa as marcações (sem criar
/// cards de PO) e o endpoint de métricas devolve DORA/próprias derivadas — com "não medida" onde falta insumo.
/// </summary>
public sealed class DeliveryDailyMetricsApiTests
{
    [Fact]
    public async Task DailyBriefingCapturePersistsSummaryAndMetricsAreDerived()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"daily-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "daily.db");
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
                var task = await CreateTaskAsync(client, project.Id, demand.Id, "Implementar cobrança",
                    DateTimeOffset.UtcNow.AddDays(30), timeout.Token);
                using (var move = await client.PostAsJsonAsync(
                    $"/api/v1/tasks/{task.Id}/moves",
                    new MoveTaskRequest("blocked", "Aguardando acesso ao banco de dados de homologação"),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, move.StatusCode);
                }

                // DEL-03: briefing pré-daily coerente (primeira daily, sinais de atenção, perguntas).
                var briefing = await client.GetFromJsonAsync<DailyBriefingContract>(
                    $"/api/v1/deliveries/{project.Id}/daily/briefing", timeout.Token);
                Assert.NotNull(briefing);
                Assert.True(briefing!.IsFirstDaily);
                Assert.Contains(briefing.ItemsNeedingAttention, s => s.Code == "pending_db_access");
                Assert.NotEmpty(briefing.RecommendedQuestions);
                Assert.Equal("red", briefing.Snapshot.Health);

                // DEL-03: captura de marcação tipada persiste (append-only). Tipo inválido → 400.
                using (var bad = await client.PostAsJsonAsync(
                    $"/api/v1/deliveries/{project.Id}/daily/captures",
                    new DailyCaptureRequest("nope", "x", null), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                }

                var capture = await CaptureAsync(client, project.Id,
                    new DailyCaptureRequest("access", "Falta acesso ao banco de homologação", "Mateus"), timeout.Token);
                Assert.Equal("access", capture.Kind);
                var second = await CaptureAsync(client, project.Id,
                    new DailyCaptureRequest("risk", "Prazo apertado", null), timeout.Token);
                Assert.NotEqual(capture.Id, second.Id);

                // DEL-03: resumo pós-daily agrupa por tipo e NÃO cria cards de PO.
                var summary = await client.GetFromJsonAsync<DailySummaryContract>(
                    $"/api/v1/deliveries/{project.Id}/daily/summary", timeout.Token);
                Assert.NotNull(summary);
                Assert.False(summary!.CreatedPoCards);
                Assert.Equal(2, summary.TotalCaptures);
                Assert.Contains(summary.ByKind, k => k.Kind == "access" && k.Count == 1);
                Assert.Contains(summary.ByKind, k => k.Kind == "risk" && k.Count == 1);

                // A captura é durável: o resumo continua íntegro em uma nova leitura.
                var summaryAgain = await client.GetFromJsonAsync<DailySummaryContract>(
                    $"/api/v1/deliveries/{project.Id}/daily/summary", timeout.Token);
                Assert.Equal(2, summaryAgain!.TotalCaptures);

                // DEL-06: métricas derivadas — DORA + próprias; "não medida" onde falta insumo de deploy.
                var metrics = await client.GetFromJsonAsync<DeliveryMetricsContract>(
                    $"/api/v1/deliveries/{project.Id}/metrics", timeout.Token);
                Assert.NotNull(metrics);
                var dora = metrics!.Dora.ToDictionary(m => m.Key, StringComparer.Ordinal);
                Assert.False(dora["change_lead_time"].Measured);
                Assert.False(dora["deployment_frequency"].Measured);
                Assert.False(dora["failed_deploy_recovery"].Measured);
                var own = metrics.Own.ToDictionary(m => m.Key, StringComparer.Ordinal);
                Assert.True(own["documentation_coverage"].Measured);
                Assert.True(own["planned_vs_realized_value"].Measured);
                Assert.Equal("1", own["time_waiting_access"].Value); // task bloqueada em acesso
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<DailyCaptureContract> CaptureAsync(
        HttpClient client, string projectId, DailyCaptureRequest body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/deliveries/{projectId}/daily/captures", body, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DailyCaptureContract>(token))!;
    }

    private static async Task<DemandContract> CreateDemandAsync(
        HttpClient client, string projectId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/demands",
            new CreateDemandRequest(projectId, "Cobrança recorrente", "Entregar cobrança recorrente.",
                null, "high"), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DemandContract>(token))!;
    }

    private static async Task<BoardTaskContract> CreateTaskAsync(
        HttpClient client, string projectId, string demandId, string title, DateTimeOffset? dueAt,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/tasks",
            new CreateTaskRequest(projectId, title, "Instrução de execução do card.", demandId,
                "high", null, dueAt), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BoardTaskContract>(token))!;
    }

    private static async Task CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(
        HttpClient client, CancellationToken token)
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
}
