using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Governance;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Workflows;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Modules.Workflows.Contracts;
using Harness.Persistence.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Workflows;

public sealed class WorkflowConsistencyApiTests
{
    [Fact]
    public async Task LayeredVerificationPassesHealthyRunsAndDetectsTamperedState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"consistency-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "consistency.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);

        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var profile = await CreateProfileAsync(client, timeout.Token);
                var tenantId = profile.Id;
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                var templates = (await client.GetFromJsonAsync<WorkflowTemplatePage>(
                    "/api/v1/workflow-templates?limit=50", timeout.Token))!;
                var standard = templates.Items.Single(template =>
                    template.Name.StartsWith("Entrega padrão", StringComparison.Ordinal));
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
                using var bindingResponse = await client.PostAsJsonAsync(
                    $"/api/v1/workflows/{defaultBinding.Id}/operation-mode",
                    new SetWorkflowOperationModeRequest(
                        "semiautonomous",
                        ["Aprovação de Homologação"],
                        "Aceite de risco para verificação de consistência."),
                    timeout.Token);
                bindingResponse.EnsureSuccessStatusCode();
                var workflow = (await bindingResponse.Content
                    .ReadFromJsonAsync<WorkflowContract>(timeout.Token))!;
                using var runResponse = await client.PostAsJsonAsync(
                    "/api/v1/workflow-runs",
                    new CreateWorkflowRunRequest(workflow.Id),
                    timeout.Token);
                runResponse.EnsureSuccessStatusCode();
                var run = (await runResponse.Content
                    .ReadFromJsonAsync<WorkflowRunContract>(timeout.Token))!;

                using var healthyResponse = await client.PostAsJsonAsync(
                    $"/api/v1/workflow-runs/{run.Id}/consistency-checks",
                    new { },
                    timeout.Token);
                healthyResponse.EnsureSuccessStatusCode();
                var healthy = (await healthyResponse.Content
                    .ReadFromJsonAsync<WorkflowConsistencyCheckResponse>(timeout.Token))!;
                Assert.True(healthy.Consistent);
                Assert.Equal(
                    ["deterministic", "structural", "semantic"],
                    healthy.Layers.Select(layer => layer.Layer));
                Assert.All(healthy.Layers, layer => Assert.Equal("passed", layer.State));

                var dispatcher = app.Services.GetRequiredService<SqliteWriteDispatcher>();
                await dispatcher.ExecuteAsync(async (connection, token) =>
                {
                    await using var update = connection.CreateCommand();
                    update.CommandText =
                        "UPDATE workflow_phase_runs SET state='completed',completed_at=activated_at " +
                        "WHERE workflow_run_id=$run AND phase_order=1;";
                    update.Parameters.AddWithValue("$run", run.Id);
                    return await update.ExecuteNonQueryAsync(token);
                }, timeout.Token);

                using var tamperedResponse = await client.PostAsJsonAsync(
                    $"/api/v1/workflow-runs/{run.Id}/consistency-checks",
                    new { },
                    timeout.Token);
                tamperedResponse.EnsureSuccessStatusCode();
                var tampered = (await tamperedResponse.Content
                    .ReadFromJsonAsync<WorkflowConsistencyCheckResponse>(timeout.Token))!;
                Assert.False(tampered.Consistent);
                var deterministic = tampered.Layers.Single(layer => layer.Layer == "deterministic");
                Assert.Equal("failed", deterministic.State);
                Assert.Contains(
                    deterministic.Findings,
                    finding => finding.Code is "phase_completion_invariant" or "active_phase_invariant");
                Assert.Equal(
                    "skipped",
                    tampered.Layers.Single(layer => layer.Layer == "semantic").State);

                var audit = (await client.GetFromJsonAsync<AuditEventPage>(
                    "/api/v1/audit-events?action=workflow.consistencyVerified&limit=50",
                    timeout.Token))!;
                Assert.Equal(2, audit.Items.Count);
                Assert.All(audit.Items, item => Assert.Equal(run.Id, item.TargetId));
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
