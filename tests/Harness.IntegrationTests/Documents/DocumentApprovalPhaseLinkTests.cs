using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Projects;
using Harness.Host.Workflows;
using Harness.Modules.Documents.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Modules.Workflows.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Documents;

/// <summary>
/// O portão da F9: aprovar um documento na tela REFLETE na etapa.
///
/// O objetivo de uma etapa sobe por degraus (`pending → executed → validated →
/// approved`) e o condutor de fase para em `validated` de propósito: o último é
/// decisão de pessoa. Só que nenhuma superfície do produto dava esse degrau — o
/// dono aprovava o documento, o documento virava "aprovado", e a etapa continuava
/// parada esperando um clique inexistente.
///
/// Estes dois cenários fixam as duas metades da regra: a aprovação do dono anda o
/// último degrau; e ela NÃO fabrica os degraus de trabalho e de conferência que
/// vêm antes.
/// </summary>
public sealed class DocumentApprovalPhaseLinkTests
{
    [Fact]
    public async Task OwnerApprovalOfDocumentAdvancesPhaseObjectiveToApproved()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"approval-phase-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "approval-phase.db");
        var catalog = Path.Combine(root, "catalog");
        Directory.CreateDirectory(root);

        try
        {
            await using var app = CreateHost(database, catalog);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var context = await SeedRunningProjectAsync(client, timeout.Token);

                // A etapa ativa declara os artefatos que ela exige; o primeiro é
                // o que a equipe vai produzir e o dono vai aprovar.
                var phase = await ActivePhaseAsync(client, context.RunId, timeout.Token);
                var deliverable = phase.Deliverables[0];
                // Etapa ativa, artefato ainda sem trabalho: "não começou".
                Assert.Equal("notStarted", deliverable.Status);

                // A equipe trabalha: o artefato foi produzido e conferido.
                await AdvanceObjectiveAsync(client, context.RunId, "document-1", "executed", timeout.Token);
                await AdvanceObjectiveAsync(client, context.RunId, "document-1", "validated", timeout.Token);
                Assert.Equal(
                    "inReview",
                    (await ActivePhaseAsync(client, context.RunId, timeout.Token))
                        .Deliverables.Single(item => item.Name == deliverable.Name).Status);

                var documentId = await CreateDocumentAwaitingApprovalAsync(
                    client, context.ProjectId, deliverable.Name, phase.Name, timeout.Token);
                var approvalId = await RequestApprovalAsync(
                    client, context.ProjectId, context.ChiefAgentId, documentId, timeout.Token);

                // O CLIQUE DO DONO na aba "Aguardando sua aprovação".
                using (var resolve = await client.PostAsJsonAsync(
                    $"/api/v1/approvals/{approvalId}/resolution",
                    new ResolveApprovalRequest("approved", "Aprovado pelo dono."), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
                }

                Assert.Equal(
                    "approved",
                    (await client.GetFromJsonAsync<DocumentContract>(
                        $"/api/v1/documents/{documentId}", timeout.Token))!.State);

                // E A ESTEIRA ANDOU: o objetivo-documento chegou ao último degrau.
                Assert.Equal(
                    "approved",
                    (await ActivePhaseAsync(client, context.RunId, timeout.Token))
                        .Deliverables.Single(item => item.Name == deliverable.Name).Status);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task OwnerApprovalDoesNotFabricateWorkOrReviewSteps()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"approval-honest-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "approval-honest.db");
        var catalog = Path.Combine(root, "catalog");
        Directory.CreateDirectory(root);

        try
        {
            await using var app = CreateHost(database, catalog);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var context = await SeedRunningProjectAsync(client, timeout.Token);

                var phase = await ActivePhaseAsync(client, context.RunId, timeout.Token);
                var deliverable = phase.Deliverables[0];

                // Nenhum degrau de trabalho foi dado: o artefato NÃO foi produzido
                // pela equipe nem conferido por ninguém.
                var documentId = await CreateDocumentAwaitingApprovalAsync(
                    client, context.ProjectId, deliverable.Name, phase.Name, timeout.Token);
                var approvalId = await RequestApprovalAsync(
                    client, context.ProjectId, context.ChiefAgentId, documentId, timeout.Token);

                using (var resolve = await client.PostAsJsonAsync(
                    $"/api/v1/approvals/{approvalId}/resolution",
                    new ResolveApprovalRequest("approved", "Aprovado pelo dono."), timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
                }

                // A decisão do dono é durável e verdadeira para o DOCUMENTO...
                Assert.Equal(
                    "approved",
                    (await client.GetFromJsonAsync<DocumentContract>(
                        $"/api/v1/documents/{documentId}", timeout.Token))!.State);

                // ...e não inventa "trabalho feito" nem "conferido" na etapa.
                Assert.Equal(
                    "notStarted",
                    (await ActivePhaseAsync(client, context.RunId, timeout.Token))
                        .Deliverables.Single(item => item.Name == deliverable.Name).Status);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed record RunContext(string ProjectId, string ChiefAgentId, string RunId);

    /// <summary>Projeto com a esteira canônica vinculada e um run em execução.</summary>
    private static async Task<RunContext> SeedRunningProjectAsync(
        HttpClient client, CancellationToken token)
    {
        using (var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), token))
        {
            profile.EnsureSuccessStatusCode();
        }

        string organizationId;
        using (var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token))
        {
            organization.EnsureSuccessStatusCode();
            organizationId = (await organization.Content
                .ReadFromJsonAsync<OrganizationResponse>(token))!.Id;
        }

        ProjectResponse project;
        using (var created = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "POSEIDON",
                Description = "Esteira canônica vinculada na criação.",
            },
            token))
        {
            created.EnsureSuccessStatusCode();
            project = (await created.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
        }

        var workflow = Assert.Single((await client.GetFromJsonAsync<WorkflowPage>(
            $"/api/v1/workflows?projectId={project.Id}", token))!.Items);

        string runId;
        using (var run = await client.PostAsJsonAsync(
            "/api/v1/workflow-runs", new CreateWorkflowRunRequest(workflow.Id), token))
        {
            run.EnsureSuccessStatusCode();
            var contract = (await run.Content.ReadFromJsonAsync<WorkflowRunContract>(token))!;
            Assert.Equal("running", contract.State);
            runId = contract.Id;
        }

        return new RunContext(project.Id, project.ChiefAgentId, runId);
    }

    private static async Task<PhaseContract> ActivePhaseAsync(
        HttpClient client, string runId, CancellationToken token)
    {
        var phases = (await client.GetFromJsonAsync<PhasePage>(
            $"/api/v1/phases?runId={runId}", token))!;
        return phases.Items.Single(phase =>
            string.Equals(phase.State, "active", StringComparison.Ordinal));
    }

    private static async Task AdvanceObjectiveAsync(
        HttpClient client, string runId, string objectiveKey, string state, CancellationToken token)
    {
        using var advance = await client.PostAsJsonAsync(
            $"/api/v1/workflow-runs/{runId}/objectives",
            new AdvanceWorkflowObjectiveRequest("phase-1", objectiveKey, state), token);
        Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
    }

    /// <summary>Documento com o nome do artefato da etapa, pronto para decisão.</summary>
    private static async Task<string> CreateDocumentAwaitingApprovalAsync(
        HttpClient client, string projectId, string title, string phaseName, CancellationToken token)
    {
        string documentId;
        using (var created = await client.PostAsJsonAsync(
            "/api/v1/documents",
            new CreateDocumentRequest(projectId, title, "spec", "# Conteúdo real", [], phaseName),
            token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            documentId = (await created.Content.ReadFromJsonAsync<DocumentContract>(token))!.Id;
        }

        using (var review = await client.PostAsJsonAsync(
            $"/api/v1/documents/{documentId}/transitions",
            new TransitionDocumentRequest("inReview"), token))
        {
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }

        return documentId;
    }

    private static async Task<string> RequestApprovalAsync(
        HttpClient client,
        string projectId,
        string chiefAgentId,
        string documentId,
        CancellationToken token)
    {
        using var request = await client.PostAsJsonAsync(
            "/api/v1/approvals",
            new CreateApprovalRequest(
                projectId, "Aprovar o artefato da etapa", "A equipe concluiu; falta sua decisão.",
                chiefAgentId, DocumentId: documentId),
            token);
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        return (await request.Content.ReadFromJsonAsync<ApprovalContract>(token))!.Id;
    }

    private static WebApplication CreateHost(string database, string catalog) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database,
         "--Harness:DocumentCatalogPath", catalog]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
