using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Workflows;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Contracts;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Workflows;

/// <summary>
/// O playbook é a 2ª fonte da verdade: a esteira de 9 fases é o workflow PADRÃO da fábrica.
/// Prova (a) que o template recomendado é a esteira do playbook com as 9 fases nomeadas; e
/// (b) que um projeto preso a um template canônico LEGADO converge por REBIND auditado —
/// preservando o binding (modo/aceites) — enquanto bindings já corretos são intocados
/// (idempotência de segunda passada).
/// </summary>
public sealed class PlaybookConvergenceTests
{
    [Fact]
    public async Task LegacyBoundProjectIsReboundToThePlaybookLane()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"playbook-convergence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "convergence.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var projectId = await SeedProjectAsync(client, timeout.Token);

                var profiles = app.Services.GetRequiredService<ILocalProfileStore>();
                var profile = (await profiles.ListAsync(timeout.Token))[0];
                var workflows = app.Services.GetRequiredService<IWorkflowCatalogStore>();
                var seeder = app.Services.GetRequiredService<WorkflowTemplateSeeder>();
                var clock = app.Services.GetRequiredService<IClock>();

                // O recomendado É a esteira do playbook, publicada com as 9 fases nomeadas.
                var recommended = await ProjectWorkflowLinker.ResolveRecommendedAsync(
                    workflows, seeder, profile.TenantId, timeout.Token);
                Assert.NotNull(recommended);
                Assert.Equal(CanonicalWorkflowTemplates.PlaybookStandardTemplate.Name, recommended.Name);

                // Projeto criado pela API já nasce vinculado (GP-09). Força o cenário LEGADO:
                // religa para o template antigo, como os projetos pré-playbook do mundo real.
                var legacy = (await workflows.ListTemplatesAsync(profile.TenantId, null, 200, timeout.Token))
                    .Single(template => template.Name == CanonicalWorkflowTemplates.TechnicalDeliveryTemplate.Name);
                var binding = (await workflows.ListBindingsAsync(
                    profile.TenantId, projectId, null, 1, timeout.Token))[0];
                _ = await workflows.RebindTemplateAsync(
                    new WorkflowTemplateRebindCommand(
                        profile.TenantId, binding.Id, projectId, legacy.Id,
                        legacy.CurrentVersionId!, profile.Id, clock.UtcNow),
                    timeout.Token);

                // Um run ATIVO da versão legada existe — o cenário real do host do dono.
                using (var legacyRun = await client.PostAsJsonAsync(
                    "/api/v1/workflow-runs", new { workflowId = binding.Id }, timeout.Token))
                {
                    legacyRun.EnsureSuccessStatusCode();
                }

                // A convergência detecta o binding legado e o religa à esteira do playbook.
                var convergence = new ProjectWorkflowConvergenceSeeder(
                    app.Services.GetRequiredService<Harness.Persistence.Abstractions.Projects.IProjectStore>(),
                    workflows, seeder,
                    app.Services.GetRequiredService<IWorkflowStore>(), clock);
                var converged = await convergence.EnsureBoundAsync(
                    profile.TenantId, profile.Id, timeout.Token);
                Assert.Equal(1, converged);

                var rebound = (await workflows.ListBindingsAsync(
                    profile.TenantId, projectId, null, 1, timeout.Token))[0];
                Assert.Equal(recommended.Id, rebound.TemplateId);
                Assert.Equal(binding.Id, rebound.Id);
                Assert.Equal(binding.OperationMode, rebound.OperationMode);

                // Segunda passada: nada a fazer — idempotente.
                Assert.Equal(0, await convergence.EnsureBoundAsync(
                    profile.TenantId, profile.Id, timeout.Token));

                // O run legado foi cancelado e um run novo nasceu RODANDO na versão do playbook.
                var runs = await workflows.ListRunsAsync(
                    profile.TenantId, binding.Id, null, 20, timeout.Token);
                var active = Assert.Single(runs, run => run.State is "running" or "paused");
                Assert.Equal(recommended.CurrentVersionId, active.VersionId);
                Assert.Contains(runs, run => run.State == "cancelled");

                // Regressão da Fase 9: uma versão publicada com as mesmas fases, mas sem
                // transições/evidências, é drift canônico. O seeder publica a versão corrigida;
                // a convergência atualiza tanto o binding quanto o run ativo, sem deixar o
                // próximo run voltar à versão obsoleta.
                var driftAt = clock.UtcNow;
                var driftVersionId = UlidValue.New(driftAt).ToString();
                var drift = WorkflowCatalogApplicationService.CreateVersion(
                    recommended.Id,
                    driftVersionId,
                    new PublishWorkflowVersionRequest(
                        CanonicalWorkflowTemplates.PlaybookStandardTemplate.Phases,
                        CanonicalWorkflowTemplates.PlaybookStandardTemplate.GatesByPhase,
                        Changelog: "Drift de regressão sem transições."),
                    driftAt);
                _ = await workflows.PublishVersionAsync(
                    new WorkflowVersionPublishCommand(
                        profile.TenantId,
                        recommended.Id,
                        driftVersionId,
                        drift.Hierarchy.Phases.Select(phase => new WorkflowPhaseCreateInput(
                            phase.Id,
                            phase.Key,
                            phase.Name,
                            phase.Order,
                            phase.Objectives.Select(objective => new WorkflowObjectiveCreateInput(
                                objective.Id,
                                objective.Key,
                                objective.Name,
                                objective.Kind,
                                objective.Weight)).ToArray(),
                            phase.Gates.Select(gate => new WorkflowGateCreateInput(
                                gate.Id,
                                gate.ObjectiveId,
                                gate.Key,
                                gate.Name,
                                gate.MinimumRequiredState,
                                gate.RequiredObjectiveIds)).ToArray())).ToArray(),
                        "{}",
                        null,
                        "{}",
                        drift.Hierarchy.Changelog,
                        driftAt),
                    timeout.Token);

                Assert.Equal(1, await seeder.EnsureSeededAsync(
                    profile.TenantId, timeout.Token));
                var repairedTemplate = await workflows.GetTemplateAsync(
                    profile.TenantId, recommended.Id, timeout.Token);
                Assert.NotNull(repairedTemplate);
                Assert.NotEqual(driftVersionId, repairedTemplate.CurrentVersionId);
                var repairedVersion = await workflows.GetVersionAsync(
                    profile.TenantId, repairedTemplate.CurrentVersionId!, timeout.Token);
                Assert.NotNull(repairedVersion);
                Assert.Contains("Arquivada", repairedVersion.TransitionsJson, StringComparison.Ordinal);

                Assert.Equal(1, await convergence.EnsureBoundAsync(
                    profile.TenantId, profile.Id, timeout.Token));
                var upgradedBinding = (await workflows.ListBindingsAsync(
                    profile.TenantId, projectId, null, 1, timeout.Token))[0];
                Assert.Equal(repairedTemplate.CurrentVersionId, upgradedBinding.ActiveVersionId);
                runs = await workflows.ListRunsAsync(
                    profile.TenantId, binding.Id, null, 20, timeout.Token);
                active = Assert.Single(runs, run => run.State is "running" or "paused");
                Assert.Equal(repairedTemplate.CurrentVersionId, active.VersionId);
                Assert.Equal(0, await convergence.EnsureBoundAsync(
                    profile.TenantId, profile.Id, timeout.Token));

                // O rebind é fato auditável no ledger.
                var ledger = app.Services.GetRequiredService<IAuditEventStore>();
                var events = await ledger.ListAsync(
                    new AuditEventQuery(profile.TenantId, null, 200), timeout.Token);
                Assert.Contains(events, entry => entry.Action == "workflow.templateRebound");

                // E o chat/board passam a enxergar as fases do playbook via a versão ativa.
                using var response = await client.GetAsync(
                    $"/api/v1/workflows?projectId={projectId}", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var item = body.RootElement.GetProperty("items").EnumerateArray().First();
                Assert.Equal(recommended.Id, item.GetProperty("templateId").GetString());
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Operador", null, null, "pt-BR"),
            token);
        profile.EnsureSuccessStatusCode();

        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        organization.EnsureSuccessStatusCode();
        using var organizationBody = JsonDocument.Parse(
            await organization.Content.ReadAsStringAsync(token));
        var organizationId = organizationBody.RootElement.GetProperty("id").GetString()!;

        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Projeto da convergência canônica.",
            },
            token);
        project.EnsureSuccessStatusCode();
        using var projectBody = JsonDocument.Parse(
            await project.Content.ReadAsStringAsync(token));
        return projectBody.RootElement.GetProperty("id").GetString()!;
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
