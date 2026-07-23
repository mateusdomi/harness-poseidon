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
/// Prova end-to-end (SQLite in-process) da Central de Entregas: portfólio + visão de atenção (DEL-01),
/// Projeto 360 (DEL-02) e histórico de previsão APPEND-ONLY (DEL-09).
/// </summary>
public sealed class DeliveryApiTests
{
    [Fact]
    public async Task PortfolioOverviewAndAppendOnlyForecastHistory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"delivery-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "delivery.db");
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

                // Uma demanda (marco) com duas tasks; uma delas bloqueada aguardando acesso ao banco.
                var demand = await CreateDemandAsync(client, project.Id, timeout.Token);
                var task1 = await CreateTaskAsync(
                    client, project.Id, demand.Id, "Implementar serviço de cobrança",
                    DateTimeOffset.UtcNow.AddDays(30), timeout.Token);
                await CreateTaskAsync(
                    client, project.Id, demand.Id, "Integrar gateway", null, timeout.Token);

                using (var move = await client.PostAsJsonAsync(
                    $"/api/v1/tasks/{task1.Id}/moves",
                    new MoveTaskRequest("blocked", "Aguardando acesso ao banco de dados de homologação"),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, move.StatusCode);
                }

                // DEL-01: portfólio lista a entrega (por projeto), não cards.
                var portfolio = await client.GetFromJsonAsync<DeliveryPortfolioContract>(
                    "/api/v1/deliveries", timeout.Token);
                Assert.NotNull(portfolio);
                var delivery = Assert.Single(portfolio!.Deliveries, d => d.DeliveryId == project.Id);
                Assert.Equal(project.Id, delivery.ProjectId);
                Assert.Equal("red", delivery.Health); // sinal crítico (acesso a banco pendente)
                Assert.Equal(1, delivery.MilestonesTotal);
                Assert.Equal(0, delivery.MilestonesDone);
                Assert.Contains(delivery.AttentionSignals, s => s.Code == "pending_db_access");
                Assert.Contains(delivery.AttentionSignals, s => s.Code == "missing_doc");

                // Visão "Precisa da minha atenção".
                var attention = await client.GetFromJsonAsync<DeliveryPortfolioContract>(
                    "/api/v1/deliveries?view=attention", timeout.Token);
                Assert.Equal("attention", attention!.View);
                Assert.Contains(attention.Deliveries, d => d.DeliveryId == project.Id);

                // DEL-02: Projeto 360 com seções coerentes.
                var overview = await client.GetFromJsonAsync<Delivery360Contract>(
                    $"/api/v1/deliveries/{project.Id}/overview", timeout.Token);
                Assert.NotNull(overview);
                Assert.Equal(project.Id, overview!.ProjectId);
                Assert.Equal(project.Key, overview.ExecutiveSummary.Key);
                Assert.Equal(10, overview.TechnicalHealth.Indicators.Count);
                Assert.Equal(4, overview.Documentation.Expected);
                Assert.Equal(0, overview.Documentation.Present);
                Assert.True(overview.RisksAndDependencies.BlockedTaskCount >= 1);
                Assert.NotNull(overview.PlanAndMilestones.Forecast);

                // DEL-09: cada POST anexa uma NOVA previsão; o histórico nunca é sobrescrito.
                var first = await AppendForecastAsync(client, project.Id, timeout.Token);
                var second = await AppendForecastAsync(client, project.Id, timeout.Token);
                Assert.NotEqual(first.Id, second.Id);

                var history = await client.GetFromJsonAsync<DeliveryForecastHistoryContract>(
                    $"/api/v1/deliveries/{project.Id}/forecast", timeout.Token);
                Assert.NotNull(history);
                Assert.Equal(2, history!.History.Count); // duas linhas, nunca sobrescrita
                Assert.Equal(second.Id, history.Latest!.Id); // mais recente primeiro
                Assert.All(history.History, f => Assert.NotEmpty(f.Basis));

                // O overview agora traz o histórico de previsão (auditável).
                var overviewAfter = await client.GetFromJsonAsync<Delivery360Contract>(
                    $"/api/v1/deliveries/{project.Id}/overview", timeout.Token);
                Assert.Equal(2, overviewAfter!.PlanAndMilestones.ForecastHistory.Count);
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<DeliveryForecastContract> AppendForecastAsync(
        HttpClient client, string projectId, CancellationToken token)
    {
        using var response = await client.PostAsync(
            $"/api/v1/deliveries/{projectId}/forecast", null, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeliveryForecastContract>(token))!;
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
