using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Coordination;

public sealed class DemandPlanApiTests
{
    [Fact]
    public async Task GeneratePersistMaterializeSurvivesRestartAndIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"plan-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "plan.db");
        Directory.CreateDirectory(root);
        var cookies = new CookieContainer();
        string projectId;
        string demandId;
        string planId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    var profile = await CreateProfileAsync(client, timeout.Token);
                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                    projectId = project.Id;

                    using var demandResponse = await client.PostAsJsonAsync("/api/v1/demands",
                        new CreateDemandRequest(projectId, "CAT-04 Publicar recursos",
                            "Investigar a viabilidade técnica; implementar o backend do serviço e a tela React de UI; integrar com o gateway de pagamento usando a credencial externa de homologação.",
                            null, "high"), timeout.Token);
                    var demand = await demandResponse.Content.ReadFromJsonAsync<DemandContract>(timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, demandResponse.StatusCode);
                    demandId = demand!.Id;

                    // GET antes de gerar → 404 (inerte).
                    using (var missing = await client.GetAsync($"/api/v1/demands/{demandId}/plan", timeout.Token))
                        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

                    // Gera + persiste o plano.
                    DemandPlanContract plan;
                    using (var generateResponse = await client.PostAsJsonAsync(
                        $"/api/v1/demands/{demandId}/plan",
                        new GeneratePlanRequest(["O endpoint responde 200.", "A tela renderiza os dados."]),
                        timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, generateResponse.StatusCode);
                        plan = (await generateResponse.Content.ReadFromJsonAsync<DemandPlanContract>(timeout.Token))!;
                    }

                    planId = plan.Id;
                    Assert.Equal("CAT-04", plan.FeatureId);
                    Assert.Equal("proposed", plan.Status);
                    Assert.Null(plan.MaterializedAt);
                    // spike + human_gate + backend + frontend + integração = 5 cards.
                    Assert.Contains(plan.Cards, c => c.CardType == "human_gate");
                    Assert.Contains(plan.Cards, c => c.CardType == "spike");
                    Assert.Contains(plan.Cards, c => c.CardType == "agent_task" && c.RequiredRole == "backend-specialist");
                    Assert.Contains(plan.Cards, c => c.CardType == "agent_task" && c.RequiredRole == "frontend-specialist");
                    Assert.Contains(plan.Cards, c => c.CardType == "agent_task" && c.RequiredRole == "critic");

                    // Regenerar é idempotente: devolve o MESMO plano (200) sem duplicar.
                    using (var regenerate = await client.PostAsJsonAsync(
                        $"/api/v1/demands/{demandId}/plan", new GeneratePlanRequest(), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, regenerate.StatusCode);
                        var again = (await regenerate.Content.ReadFromJsonAsync<DemandPlanContract>(timeout.Token))!;
                        Assert.Equal(planId, again.Id);
                        Assert.Equal(plan.Cards.Count, again.Cards.Count);
                    }

                    // Nenhuma task foi criada ainda (gerar é inerte).
                    var beforeMaterialize = await client.GetFromJsonAsync<TaskPage>(
                        $"/api/v1/tasks?projectId={projectId}&demandId={demandId}", timeout.Token);
                    Assert.Empty(beforeMaterialize?.Items ?? []);
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            // Reinício: o plano sobrevive e continua legível.
            await using (var restarted = CreateHost(database))
            {
                await restarted.StartAsync(timeout.Token);
                try
                {
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = Address(restarted.Services) };

                    var recovered = await client.GetFromJsonAsync<DemandPlanContract>(
                        $"/api/v1/demands/{demandId}/plan", timeout.Token);
                    Assert.Equal(planId, recovered?.Id);
                    Assert.Equal("proposed", recovered?.Status);
                    var cardCount = recovered!.Cards.Count;

                    // Materializa: cada card proposto vira uma work_task com seu card_type.
                    MaterializePlanResult materialized;
                    using (var response = await client.PostAsync(
                        $"/api/v1/demands/{demandId}/plan/materialize", null, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        materialized = (await response.Content.ReadFromJsonAsync<MaterializePlanResult>(timeout.Token))!;
                    }

                    Assert.False(materialized.AlreadyMaterialized);
                    Assert.Equal("materialized", materialized.Status);
                    Assert.Equal(cardCount, materialized.Cards.Count);
                    Assert.All(materialized.Cards, c => Assert.NotNull(c.TaskId));

                    var tasks = await client.GetFromJsonAsync<TaskPage>(
                        $"/api/v1/tasks?projectId={projectId}&demandId={demandId}", timeout.Token);
                    Assert.Equal(cardCount, tasks!.Items.Count);

                    // human_gate criado, porém NÃO auto-despachável (card_type protege o dispatch).
                    var humanGateTaskId = materialized.Cards.Single(c => c.CardType == "human_gate").TaskId!;
                    var exported = await client.GetStringAsync(
                        $"/api/v1/tasks/export.csv?projectId={projectId}&demandId={demandId}", timeout.Token);
                    Assert.Contains("human_gate", exported, StringComparison.Ordinal);
                    Assert.Contains("spike", exported, StringComparison.Ordinal);
                    Assert.Contains(humanGateTaskId, exported, StringComparison.Ordinal);
                    // Todos os cards nascem em backlog (inertes até a triagem promover a ready).
                    Assert.All(tasks.Items, t => Assert.Equal("backlog", t.State));

                    // Plano agora materializado.
                    var afterPlan = await client.GetFromJsonAsync<DemandPlanContract>(
                        $"/api/v1/demands/{demandId}/plan", timeout.Token);
                    Assert.Equal("materialized", afterPlan?.Status);
                    Assert.NotNull(afterPlan?.MaterializedAt);

                    // Materializar de novo é idempotente: NÃO recria tasks.
                    using (var second = await client.PostAsync(
                        $"/api/v1/demands/{demandId}/plan/materialize", null, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
                        var again = (await second.Content.ReadFromJsonAsync<MaterializePlanResult>(timeout.Token))!;
                        Assert.True(again.AlreadyMaterialized);
                    }

                    var afterSecond = await client.GetFromJsonAsync<TaskPage>(
                        $"/api/v1/tasks?projectId={projectId}&demandId={demandId}", timeout.Token);
                    Assert.Equal(cardCount, afterSecond!.Items.Count);
                }
                finally { await restarted.StopAsync(timeout.Token); }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<ProfileResponse> CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(HttpClient client, string organizationId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest
        { OrganizationId = organizationId, Name = "Poseidon", Key = "POSEIDON", Description = "Backend" }, token);
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
