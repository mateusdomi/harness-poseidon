using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Workflows;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Modules.Workflows.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Projects;

/// <summary>
/// GP-09 + RN-02: um projeto criado sem workflow explícito nasce já vinculado ao template recomendado
/// publicado (o "Software Delivery Standard"), para que o caminho dourado do Chief não fique
/// bloqueado em um workflow ausente. Um override explícito (ULID) seleciona outro template; ausência
/// OU string vazia caem no recomendado — RN-02: nunca há opt-out, um projeto nunca fica sem workflow.
/// </summary>
public sealed class ProjectWorkflowPreselectionTests
{
    [Fact]
    public async Task CreatingAProjectPreselectsTheRecommendedPublishedWorkflow()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"gp09-recommended-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "gp09.db");
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

                var recommended = await RecommendedTemplateAsync(client, timeout.Token);

                var project = await CreateProjectAsync(
                    client, organization.Id, "POSEIDON", null, timeout.Token);

                var workflows = (await client.GetFromJsonAsync<WorkflowPage>(
                    $"/api/v1/workflows?projectId={project.Id}", timeout.Token))!;
                var workflow = Assert.Single(workflows.Items);
                Assert.Equal(project.Id, workflow.ProjectId);
                Assert.Equal(recommended.Id, workflow.TemplateId);
                Assert.Equal(recommended.CurrentVersionId, workflow.ActiveVersionId);
                // Fase 1E: a versao canonica nao declara modo, entao o vinculo HERDA o do
                // projeto — que nasce autonomo. Vincular um template nao torna o projeto manual.
                Assert.Equal("autonomous", workflow.OperationMode);
                Assert.Empty(workflow.RiskAcceptances);

                // O template recomendado é o canônico "Software Delivery Standard".
                Assert.Equal(CanonicalWorkflowTemplates.Recommended.Name, recommended.Name);

                // Uma execução pode iniciar imediatamente: o projeto está pronto de fábrica.
                using var run = await client.PostAsJsonAsync(
                    "/api/v1/workflow-runs",
                    new CreateWorkflowRunRequest(workflow.Id),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Created, run.StatusCode);
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
    public async Task CreatingAProjectWithAnExplicitTemplateLinksTheChosenOne()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"gp09-override-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "gp09.db");
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

                var recommended = await RecommendedTemplateAsync(client, timeout.Token);
                // Escolhe um template canônico DIFERENTE do recomendado como override.
                var templates = (await client.GetFromJsonAsync<WorkflowTemplatePage>(
                    "/api/v1/workflow-templates?limit=50", timeout.Token))!;
                var chosen = templates.Items.First(template =>
                    template.CurrentVersionId is not null && template.Id != recommended.Id);

                var project = await CreateProjectAsync(
                    client, organization.Id, "OVERRIDE", chosen.Id, timeout.Token);

                var workflows = (await client.GetFromJsonAsync<WorkflowPage>(
                    $"/api/v1/workflows?projectId={project.Id}", timeout.Token))!;
                var workflow = Assert.Single(workflows.Items);
                Assert.Equal(chosen.Id, workflow.TemplateId);
                Assert.NotEqual(recommended.Id, workflow.TemplateId);

                // Um override para um template inexistente é rejeitado com 404, sem criar projeto.
                using var missing = await client.PostAsJsonAsync(
                    "/api/v1/projects",
                    new CreateProjectRequest
                    {
                        OrganizationId = organization.Id,
                        Name = "Ghost",
                        Key = "GHOST",
                        Description = "Sem template",
                        WorkflowTemplateId = Harness.SharedKernel.Identifiers.UlidValue.New(
                            DateTimeOffset.UtcNow).ToString(),
                    },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

                // Um override que não é ULID é rejeitado com 400.
                using var invalid = await client.PostAsJsonAsync(
                    "/api/v1/projects",
                    new CreateProjectRequest
                    {
                        OrganizationId = organization.Id,
                        Name = "Bad",
                        Key = "BAD",
                        Description = "Id inválido",
                        WorkflowTemplateId = "not-a-ulid",
                    },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
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
    public async Task CreatingAProjectWithAnEmptyTemplateStillLinksTheRecommendedWorkflow()
    {
        // RN-02: não há opt-out. Uma string vazia (o antigo "opt-out" do GP-09) cai no template
        // recomendado — é impossível criar um projeto sem workflow.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"rn02-empty-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "gp09.db");
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

                var recommended = await RecommendedTemplateAsync(client, timeout.Token);
                var project = await CreateProjectAsync(
                    client, organization.Id, "EMPTY", string.Empty, timeout.Token);

                var workflows = (await client.GetFromJsonAsync<WorkflowPage>(
                    $"/api/v1/workflows?projectId={project.Id}", timeout.Token))!;
                var workflow = Assert.Single(workflows.Items);
                Assert.Equal(recommended.Id, workflow.TemplateId);
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

    private static async Task<WorkflowTemplateContract> RecommendedTemplateAsync(
        HttpClient client, CancellationToken token)
    {
        var templates = (await client.GetFromJsonAsync<WorkflowTemplatePage>(
            "/api/v1/workflow-templates?limit=50", token))!;
        return templates.Items.Single(template =>
            string.Equals(
                template.Name,
                CanonicalWorkflowTemplates.Recommended.Name,
                StringComparison.Ordinal));
    }

    private static async Task<ProfileResponse> CreateProfileAsync(
        HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"),
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(
        HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(
        HttpClient client, string organizationId, string key, string? workflowTemplateId,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = key,
                Key = key,
                Description = "Backend",
                WorkflowTemplateId = workflowTemplateId,
            },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
    }

    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
