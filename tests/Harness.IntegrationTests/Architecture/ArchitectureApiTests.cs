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
/// Prova end-to-end (SQLite in-process) do Architecture Hub: modelo estruturado + dependências +
/// view (ARC-01); catálogo/mapas/heatmap (ARC-02); Sistema 360 (ARC-03); e edição humana da
/// arquitetura proposta com LOCK que impede sobrescrita por agente, aplicar-com-justificativa,
/// histórico e rollback, mantendo proposto e vigente SEPARADOS (ARC-05).
/// </summary>
public sealed class ArchitectureApiTests
{
    [Fact]
    public async Task ModelDependenciesViewMapsSystem360AndHumanEditing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"architecture-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "architecture.db");
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

                // ARC-01: modelo estruturado — dois sistemas, um componente, relacionamentos.
                var billing = await CreateElementAsync(client, project.Id, "system", "Billing", timeout.Token);
                var gateway = await CreateElementAsync(client, project.Id, "system", "Gateway", timeout.Token);
                var ledger = await CreateElementAsync(client, project.Id, "component", "Ledger", timeout.Token);
                await CreateRelationshipAsync(client, project.Id, billing.Id, gateway.Id, "depends-on", timeout.Token);
                await CreateRelationshipAsync(client, project.Id, billing.Id, ledger.Id, "contains", timeout.Token);

                // ARC-01: "quem depende de Gateway?" -> Billing (direto).
                var dependents = await client.GetFromJsonAsync<ArchitectureDependencyQueryContract>(
                    $"/api/v1/architecture/elements/{gateway.Id}/dependents", timeout.Token);
                Assert.Equal(1, dependents!.DirectCount);
                Assert.Contains(dependents.Dependents, d => d.ElementId == billing.Id && d.Direct);

                // ARC-01: view como SELEÇÃO (nunca imagem) — só os dois sistemas + a aresta entre eles.
                var view = await CreateViewAsync(client, project.Id, [billing.Id, gateway.Id], timeout.Token);
                Assert.Equal(2, view!.Elements.Count);
                Assert.Single(view.Relationships); // 'contains' com Ledger fica de fora da seleção

                // ARC-02/03: metadados dos sistemas (heatmaps e 360 derivam estritamente destes fatos).
                await SetMetadataAsync(client, billing.Id, criticality: "critical", domain: "payments",
                    capabilities: ["billing"], owner: null, lifecycle: "obsolete", token: timeout.Token);
                await SetMetadataAsync(client, gateway.Id, criticality: "high", domain: "payments",
                    capabilities: ["integration"], owner: "platform", lifecycle: "active", token: timeout.Token);

                // ARC-02: catálogo, mapas, grafo de integração e heatmap.
                var catalog = await client.GetFromJsonAsync<SystemCatalogContract>(
                    "/api/v1/architecture/systems", timeout.Token);
                Assert.Equal(2, catalog!.Total);
                var billingEntry = Assert.Single(catalog.Systems, s => s.Id == billing.Id);
                Assert.Contains("critical", billingEntry.HeatSignals);
                Assert.Contains("no-owner", billingEntry.HeatSignals);
                Assert.Contains("tech-obsolete", billingEntry.HeatSignals);

                var integration = await client.GetFromJsonAsync<IntegrationGraphContract>(
                    "/api/v1/architecture/maps/integration", timeout.Token);
                Assert.Equal(2, integration!.SystemCount);
                Assert.Equal(1, integration.EdgeCount); // Billing -> Gateway (sistema->sistema)

                var domains = await client.GetFromJsonAsync<DomainMapContract>(
                    "/api/v1/architecture/maps/domains", timeout.Token);
                Assert.Contains(domains!.Domains, d => d.Domain == "payments" && d.SystemCount == 2);

                var heatmap = await client.GetFromJsonAsync<SystemHeatmapContract>(
                    "/api/v1/architecture/maps/heatmap", timeout.Token);
                Assert.True(heatmap!.SignalTotals["critical"] >= 1);

                // ARC-03: Sistema 360 com seções tipadas.
                var overview = await client.GetFromJsonAsync<System360Contract>(
                    $"/api/v1/architecture/systems/{billing.Id}/overview", timeout.Token);
                Assert.Equal("payments", overview!.Business.Domain);
                Assert.Equal("critical", overview.Business.Criticality);
                Assert.Single(overview.Technology.Containers);   // Ledger via 'contains'
                Assert.Single(overview.Integrations.Outgoing);   // depends-on Gateway

                // ARC-05: TRAVA o Billing — uma mudança proposta por agente NÃO pode sobrescrevê-lo.
                using (var lockResponse = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/elements/{billing.Id}/lock", new LockRequest(true), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);
                }

                var lockedProposal = await CreateProposalAsync(client, project.Id, "Rename billing",
                    new { changeKind = "modify", counterpartId = billing.Id, kind = "system", name = "Billing v2" },
                    timeout.Token);
                var diff = await client.GetFromJsonAsync<ArchitectureDiffContract>(
                    $"/api/v1/architecture/proposals/{lockedProposal}/diff", timeout.Token);
                Assert.Equal(1, diff!.BlockedByLock);

                using (var apply = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/proposals/{lockedProposal}/apply",
                    new ApplyProposalRequest("try to overwrite"), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
                    var result = await apply.Content.ReadFromJsonAsync<ArchitectureApplyResultContract>(timeout.Token);
                    Assert.Equal(1, result!.SkippedLocked);
                    Assert.Empty(result.AppliedChanges);
                }

                // O elemento travado permanece intacto — o lock foi ENFORÇADO.
                var afterLock = await client.GetFromJsonAsync<ArchitectureElementContract>(
                    $"/api/v1/architecture/elements/{billing.Id}", timeout.Token);
                Assert.Equal("Billing", afterLock!.Name);

                // ARC-05: mudança crítica (remoção de sistema critical) EXIGE justificativa.
                var removalProposal = await CreateProposalAsync(client, project.Id, "Remove gateway dependency",
                    new { changeKind = "remove", counterpartId = gateway.Id, kind = "system", name = "Gateway" },
                    timeout.Token);
                using (var noJust = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/proposals/{removalProposal}/apply",
                    new ApplyProposalRequest(null), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, noJust.StatusCode);
                }

                // ARC-05: aplicar-com-justificativa em elemento NÃO travado -> registra histórico.
                var editProposal = await CreateProposalAsync(client, project.Id, "Rename gateway",
                    new { changeKind = "modify", counterpartId = gateway.Id, kind = "system", name = "Gateway v2" },
                    timeout.Token);
                using (var apply = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/proposals/{editProposal}/apply",
                    new ApplyProposalRequest("approved rename"), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
                    var result = await apply.Content.ReadFromJsonAsync<ArchitectureApplyResultContract>(timeout.Token);
                    Assert.Single(result!.AppliedChanges);
                }

                var afterEdit = await client.GetFromJsonAsync<ArchitectureElementContract>(
                    $"/api/v1/architecture/elements/{gateway.Id}", timeout.Token);
                Assert.Equal("Gateway v2", afterEdit!.Name);
                Assert.Equal(2, afterEdit.Version);

                // ARC-05: histórico + ROLLBACK para a versão 1 (nome restaurado).
                var history = await client.GetFromJsonAsync<ArchitectureHistoryContract>(
                    $"/api/v1/architecture/elements/{gateway.Id}/history", timeout.Token);
                Assert.True(history!.History.Count >= 2);

                using (var rollback = await client.PostAsJsonAsync(
                    $"/api/v1/architecture/elements/{gateway.Id}/rollback",
                    new RollbackRequest(1, "revert rename"), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
                    var restored = await rollback.Content.ReadFromJsonAsync<ArchitectureElementContract>(timeout.Token);
                    Assert.Equal("Gateway", restored!.Name);
                }
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

    private static async Task<ArchitectureViewContract> CreateViewAsync(
        HttpClient client, string projectId, IReadOnlyList<string> elementIds, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/architecture/views",
            new CreateViewRequest(projectId, "Context", "C4 context", "c4", elementIds, null, null, null), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArchitectureViewContract>(token))!;
    }

    private static async Task SetMetadataAsync(
        HttpClient client, string elementId, string criticality, string domain,
        IReadOnlyList<string> capabilities, string? owner, string lifecycle, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/architecture/systems/{elementId}/metadata",
            new { criticality, domain, capabilities, owner, lifecycleStatus = lifecycle }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> CreateProposalAsync(
        HttpClient client, string projectId, string title, object element, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/architecture/proposals",
            new { projectId, title, elements = new[] { element } }, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var proposal = await response.Content.ReadFromJsonAsync<ArchitectureProposalContract>(token);
        return proposal!.Id;
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
