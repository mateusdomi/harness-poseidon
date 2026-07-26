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

namespace Harness.IntegrationTests.Workflows;

public sealed class CanonicalWorkflowTemplateTests
{
    [Fact]
    public async Task CanonicalTemplatesAreSeededOncePerTenantAndBindableToProjects()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"canonical-workflows-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "canonical.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);

        try
        {
            string workflowId;
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                    await CreateProfileAsync(client, timeout.Token);
                    var templates = (await client.GetFromJsonAsync<WorkflowTemplatePage>(
                        "/api/v1/workflow-templates?limit=50", timeout.Token))!;
                    Assert.Equal(
                        CanonicalWorkflowTemplates.All.Select(template => template.Name)
                            .Order(StringComparer.Ordinal),
                        templates.Items.Select(template => template.Name)
                            .Order(StringComparer.Ordinal));

                    var standard = templates.Items.Single(template =>
                        template.Name.StartsWith("Entrega padrão", StringComparison.Ordinal));
                    var version = (await client.GetFromJsonAsync<WorkflowVersionContract>(
                        $"/api/v1/workflow-versions/{standard.CurrentVersionId}", timeout.Token))!;
                    Assert.Equal(
                        ["Triagem", "Análise", "Arquitetura", "Implementação", "Verificação",
                         "Homologação", "Sustentação"],
                        version.Phases);
                    Assert.Equal(5, version.GatesByPhase.Count);
                    Assert.Contains("Homologação", version.GatesByPhase.Keys);

                    var organization = await CreateOrganizationAsync(client, timeout.Token);
                    var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                    var defaultBinding = Assert.Single((await client.GetFromJsonAsync<WorkflowPage>(
                        $"/api/v1/workflows?projectId={project.Id}", timeout.Token))!.Items);
                    // O binding default é a esteira do playbook (o recomendado canônico); este
                    // cenário exercita o template LEGADO, então religa o binding explicitamente.
                    var playbookDefault = templates.Items.Single(template => template.Name.StartsWith(
                        "Esteira padrão do playbook", StringComparison.Ordinal));
                    Assert.Equal(playbookDefault.Id, defaultBinding.TemplateId);
                    await RebindBindingAsync(
                        app, project.Id, defaultBinding.Id, standard.Id, standard.CurrentVersionId!,
                        timeout.Token);
                    using var binding = await client.PostAsJsonAsync(
                        $"/api/v1/workflows/{defaultBinding.Id}/operation-mode",
                        new SetWorkflowOperationModeRequest(
                            "semiautonomous",
                            ["Aprovação de Homologação"],
                            "Aceite de risco para operação semiautônoma no dogfood."),
                        timeout.Token);
                    binding.EnsureSuccessStatusCode();
                    var workflow = (await binding.Content
                        .ReadFromJsonAsync<WorkflowContract>(timeout.Token))!;
                    workflowId = workflow.Id;
                    Assert.Equal("semiautonomous", workflow.OperationMode);
                    Assert.Single(workflow.RiskAcceptances);

                    using var runResponse = await client.PostAsJsonAsync(
                        "/api/v1/workflow-runs",
                        new CreateWorkflowRunRequest(workflow.Id),
                        timeout.Token);
                    runResponse.EnsureSuccessStatusCode();
                    var run = (await runResponse.Content
                        .ReadFromJsonAsync<WorkflowRunContract>(timeout.Token))!;
                    Assert.Equal("running", run.State);
                }
                finally
                {
                    await app.StopAsync(timeout.Token);
                }
            }

            await using (var restarted = CreateHost(database))
            {
                await restarted.StartAsync(timeout.Token);
                try
                {
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler)
                    {
                        BaseAddress = Address(restarted.Services),
                    };
                    var templates = (await client.GetFromJsonAsync<WorkflowTemplatePage>(
                        "/api/v1/workflow-templates?limit=50", timeout.Token))!;
                    Assert.Equal(
                        CanonicalWorkflowTemplates.All.Count,
                        templates.Items.Count);
                    var workflow = (await client.GetFromJsonAsync<WorkflowContract>(
                        $"/api/v1/workflows/{workflowId}", timeout.Token))!;
                    Assert.Equal("semiautonomous", workflow.OperationMode);
                }
                finally
                {
                    await restarted.StopAsync(timeout.Token);
                }
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

    private static async Task<ProfileResponse> CreateProfileAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"),
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(
        HttpClient client,
        string organizationId,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "POSEIDON",
                Description = "Backend",
                // Vazio converge para o workflow recomendado; não existe opt-out operacional.
                WorkflowTemplateId = "",
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
    /// <summary>
    /// Religa o binding default (a esteira do playbook, o recomendado canônico) para o template
    /// exercitado por este teste — o cenário continua cobrindo o template legado como variante.
    /// </summary>
    private static async Task RebindBindingAsync(
        Microsoft.AspNetCore.Builder.WebApplication app,
        string projectId,
        string bindingId,
        string templateId,
        string versionId,
        CancellationToken token)
    {
        var profiles = app.Services.GetRequiredService<Harness.Persistence.Abstractions.Identity.ILocalProfileStore>();
        var profile = (await profiles.ListAsync(token))[0];
        var store = app.Services.GetRequiredService<Harness.Persistence.Abstractions.Workflows.IWorkflowCatalogStore>();
        _ = await store.RebindTemplateAsync(
            new Harness.Persistence.Abstractions.Workflows.WorkflowTemplateRebindCommand(
                profile.TenantId, bindingId, projectId, templateId, versionId,
                profile.Id, DateTimeOffset.UtcNow),
            token);
    }

}
