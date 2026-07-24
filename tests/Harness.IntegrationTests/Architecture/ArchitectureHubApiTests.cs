using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Architecture;
using Harness.Host.Organizations;
using Harness.Host.Projects;
using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Architecture;

/// <summary>
/// Prova end-to-end (SQLite in-process) das áreas estendidas do Architecture Hub: descoberta de sistemas
/// existentes com confiança + evidência + perguntas pendentes e o rollup por sujeito (ARC-06); insights
/// de racionalização classificados com impacto — proposta, nunca ação (ARC-07); ADRs/padrões
/// reutilizáveis (ARC-08); e a integração Delivery↔Architecture — consulta de reuso ao portfólio,
/// baseline da entrega, AS-IS de produção e comparação proposta × implementação no encerramento (ARC-10).
/// </summary>
public sealed class ArchitectureHubApiTests
{
    private static readonly string[] OwnerQuestion = ["Owner team?"];
    private static readonly string[] RepoQuestions = ["Still maintained?", "Owner team?"];
    private static readonly string[] BillingCapability = ["billing"];
    private static readonly string[] IntegrationCapability = ["integration"];
    private static readonly string[] IntegrationTag = ["integration"];


    [Fact]
    public async Task DiscoveryInsightsPatternsAndDeliveryBaseline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"architecture-hub-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "architecture-hub.db");
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

                var billing = await CreateElementAsync(client, project.Id, "system", "Billing", timeout.Token);
                var gateway = await CreateElementAsync(client, project.Id, "system", "Gateway", timeout.Token);

                // ARC-06: registra descobertas com confiança + evidência + perguntas pendentes.
                var d1 = await CreateDiscoveryAsync(client, new
                {
                    projectId = project.Id,
                    systemId = billing.Id,
                    subjectName = "Billing",
                    sourceKind = "openapi",
                    field = "capabilities",
                    value = "billing",
                    confidence = "high",
                    evidence = "GET /invoices in openapi.json",
                    pendingQuestions = OwnerQuestion,
                }, timeout.Token);
                await CreateDiscoveryAsync(client, new
                {
                    projectId = project.Id,
                    systemId = billing.Id,
                    subjectName = "Billing",
                    sourceKind = "repo",
                    field = "techStack",
                    value = "dotnet",
                    confidence = "low",
                    evidence = "csproj net10.0",
                    pendingQuestions = RepoQuestions,
                }, timeout.Token);

                // Evidência é OBRIGATÓRIA em toda descoberta.
                using (var noEvidence = await client.PostAsJsonAsync("/api/v1/architecture/discoveries", new
                {
                    subjectName = "X",
                    sourceKind = "repo",
                    field = "f",
                    confidence = "low",
                }, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, noEvidence.StatusCode);
                }

                var summary = await client.GetFromJsonAsync<DiscoverySummaryListContract>(
                    "/api/v1/architecture/discoveries/summary", timeout.Token);
                var billingSubject = Assert.Single(summary!.Subjects, s => s.SystemId == billing.Id);
                Assert.Equal(2, billingSubject.DiscoveryCount);
                Assert.Equal("low", billingSubject.OverallConfidence);   // elo mais fraco
                Assert.Equal(2, billingSubject.PendingQuestions.Count);   // "Owner team?" deduplicada

                // Confirmar uma descoberta muda o rollup (deixa de ser aberta).
                using (var confirm = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/discoveries/{d1.Id}/status", new { status = "confirmed" }, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
                }

                // ARC-02/03: metadados p/ a racionalização (heatmaps/insights derivam destes fatos).
                await SetMetadataAsync(client, billing.Id, new
                {
                    criticality = "critical",
                    domain = "payments",
                    capabilities = BillingCapability,
                    lifecycleStatus = "eol",
                    owner = "team",
                }, timeout.Token);
                await SetMetadataAsync(client, gateway.Id, new
                {
                    criticality = "high",
                    domain = "integration",
                    capabilities = IntegrationCapability,
                    lifecycleStatus = "active",
                    owner = "platform",
                }, timeout.Token);
                await CreateRelationshipAsync(client, project.Id, gateway.Id, billing.Id, "depends-on", timeout.Token);

                // ARC-07: insights de racionalização — proposta com classificação e impacto.
                var insights = await client.GetFromJsonAsync<RationalizationReportContract>(
                    "/api/v1/architecture/insights", timeout.Token);
                Assert.Contains(insights!.Insights, i =>
                    i.SystemId == billing.Id && i.Category == "unsupported-tech" && i.Classification == "replace");

                // ARC-08: ADR corporativo + padrão reutilizável.
                var adr = await CreatePatternAsync(client, new
                {
                    projectId = project.Id,
                    kind = "adr",
                    title = "Use event bus for integration",
                    status = "accepted",
                    context = "Point-to-point sprawl",
                    body = "Adopt a shared bus.",
                    consequences = "Looser coupling.",
                    tags = IntegrationTag,
                }, timeout.Token);
                Assert.Equal("adr", adr!.Kind);
                var patterns = await client.GetFromJsonAsync<ArchitecturePatternListContract>(
                    "/api/v1/architecture/patterns?kind=adr", timeout.Token);
                Assert.Contains(patterns!.Items, p => p.Id == adr.Id && p.Status == "accepted");

                // ARC-10: consulta de reuso ao portfólio ANTES de uma nova entrega.
                var reuse = await client.GetFromJsonAsync<PortfolioReuseContract>(
                    "/api/v1/architecture/portfolio/reuse?capability=billing&domain=payments", timeout.Token);
                Assert.Contains(reuse!.Candidates, c => c.SystemId == billing.Id);

                // ARC-10: arquitetura aprovada vira BASELINE da entrega.
                var baseline = await CreateBaselineAsync(client, project.Id, "Release 1 baseline", timeout.Token);
                Assert.Equal("baseline", baseline!.Status);
                Assert.Equal(2, baseline.BaselineElementCount);

                // Comparar antes do AS-IS é 409 (é preciso registrar produção primeiro).
                using (var early = await client.GetAsync(
                    $"/api/v1/architecture/baselines/{baseline.Id}/comparison", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
                }

                // Produção divergiu: um novo sistema entrou fora do plano -> atualiza o AS-IS.
                var extra = await CreateElementAsync(client, project.Id, "system", "Notifier", timeout.Token);
                using (var asBuilt = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/baselines/{baseline.Id}/as-built", new { }, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, asBuilt.StatusCode);
                    var updated = await asBuilt.Content.ReadFromJsonAsync<ArchitectureBaselineContract>(timeout.Token);
                    Assert.Equal("as_built", updated!.Status);
                }

                // ARC-10: encerramento compara proposta × implementação.
                using (var close = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/baselines/{baseline.Id}/close", new { }, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, close.StatusCode);
                    var comparison = await close.Content.ReadFromJsonAsync<BaselineComparisonContract>(timeout.Token);
                    Assert.Equal(2, comparison!.Matched);           // Billing + Gateway
                    Assert.Equal(1, comparison.Unplanned);          // Notifier fora do plano
                    Assert.Contains(comparison.Drifts, d => d.ElementId == extra.Id && d.Drift == "unplanned");
                }

                var closed = await client.GetFromJsonAsync<ArchitectureBaselineContract>(
                    $"/api/v1/architecture/baselines/{baseline.Id}", timeout.Token);
                Assert.Equal("closed", closed!.Status);
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<ArchitectureElementContract> CreateElementAsync(
        HttpClient client, string projectId, string kind, string name, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/architecture/elements",
            new CreateElementRequest(projectId, kind, name, $"{name} description", null), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArchitectureElementContract>(token))!;
    }

    private static async Task CreateRelationshipAsync(
        HttpClient client, string projectId, string sourceId, string targetId, string kind, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/architecture/relationships",
            new CreateRelationshipRequest(projectId, sourceId, targetId, kind, null), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task SetMetadataAsync(HttpClient client, string elementId, object body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/architecture/systems/{elementId}/metadata", body, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<DiscoveryContract> CreateDiscoveryAsync(HttpClient client, object body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/architecture/discoveries", body, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DiscoveryContract>(token))!;
    }

    private static async Task<ArchitecturePatternContract> CreatePatternAsync(HttpClient client, object body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/architecture/patterns", body, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArchitecturePatternContract>(token))!;
    }

    private static async Task<ArchitectureBaselineContract> CreateBaselineAsync(
        HttpClient client, string projectId, string title, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/architecture/projects/{projectId}/baseline", new { title }, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArchitectureBaselineContract>(token))!;
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
        { OrganizationId = organizationId, Name = "Arquitetura", Key = "ARC", Description = "Backend" }, token);
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
