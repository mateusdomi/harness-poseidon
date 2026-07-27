using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Workflows;

/// <summary>
/// Fase 9 — o playbook 100% refletido em DADOS: (a) os tipos de card canônicos (§3) são aceitos
/// pelo board real e os de implementação são auto-despacháveis pela DoR; (b) as nove
/// especialidades (§4) nascem semeadas no catálogo por tenant; (c) o catálogo de templates
/// (§7) existe como registros-seed consultáveis com fase e tipo de card alvo.
/// </summary>
public sealed class PlaybookDataSeedTests
{
    [Fact]
    public async Task PlaybookCardTypesSpecialtiesAndTemplatesAreLiveData()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"playbook-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "playbook.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                var projectId = await SeedProjectAsync(client, timeout.Token);

                // (a) Tipos do playbook aceitos pelo board REAL, com a DoR correta por tipo.
                foreach (var (cardType, dispatchable) in new (string, bool)[]
                {
                    ("historia", true), ("tarefa", true), ("bug", true),
                    ("adr", false), ("documento", false), ("revisao", false),
                    ("gate", false), ("incidente", false), ("chamado", false),
                })
                {
                    using var created = await client.PostAsJsonAsync(
                        "/api/v1/tasks",
                        new CreateTaskRequest(
                            projectId, $"PBK-01 Card {cardType}", $"Instrução de {cardType}.",
                            CardType: cardType),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, created.StatusCode);

                    var readiness = CardReadinessEvaluator.Evaluate(
                        new CardReadinessFacts(cardType, HasInstruction: true, IsBlocked: false));
                    Assert.Equal(dispatchable, readiness.IsDispatchable);
                }

                // Card fora do vocabulário continua rejeitado (Default-FAIL).
                using var invalid = await client.PostAsJsonAsync(
                    "/api/v1/tasks",
                    new CreateTaskRequest(
                        projectId, "PBK-01 Card inválido", "Instrução.", CardType: "epico"),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

                // (b) As nove especialidades do playbook semeadas por tenant (o host desta
                // instância nasceu junto do perfil — o seed roda no startup; aqui garantimos o
                // efeito idempotente do seeder diretamente).
                var seeder = app.Services.GetRequiredService<PlaybookSpecialtySeeder>();
                var profiles = app.Services.GetRequiredService<Harness.Persistence.Abstractions.Identity.ILocalProfileStore>();
                var profile = (await profiles.ListAsync(timeout.Token))[0];
                _ = await seeder.EnsureSeededAsync(profile.TenantId, profile.Id, timeout.Token);
                Assert.Equal(0, await seeder.EnsureSeededAsync(profile.TenantId, profile.Id, timeout.Token));

                using var specialties = await client.GetAsync(
                    "/api/v1/specialties?limit=100", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, specialties.StatusCode);
                using var specialtiesBody = JsonDocument.Parse(
                    await specialties.Content.ReadAsStringAsync(timeout.Token));
                var keys = specialtiesBody.RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("key").GetString())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var expected in PlaybookSpecialtySeeder.Specialties.Select(item => item.Key))
                {
                    Assert.Contains(expected, keys);
                }

                // (c) O catálogo de templates do §7 vive como dados consultáveis.
                using var templates = await client.GetAsync(
                    "/api/v1/workflow-document-templates", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, templates.StatusCode);
                using var templatesBody = JsonDocument.Parse(
                    await templates.Content.ReadAsStringAsync(timeout.Token));
                var items = templatesBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(47, items.Length);
                var prd = Assert.Single(items, item => item.GetProperty("code").GetString() == "03");
                Assert.Equal("PRD", prd.GetProperty("name").GetString());
                Assert.Equal("2-Descoberta", prd.GetProperty("phase").GetString());
                Assert.Equal("documento", prd.GetProperty("targetCardType").GetString());
                var acceptance = Assert.Single(items, item => item.GetProperty("code").GetString() == "25");
                Assert.Equal("gate", acceptance.GetProperty("targetCardType").GetString());
                Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(
                    item.GetProperty("requiredFieldsJson").GetString())));

                await AssertPlaybookWorkflowSemanticsAsync(
                    app, projectId, profile.TenantId, timeout.Token);
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

    private static async Task AssertPlaybookWorkflowSemanticsAsync(
        WebApplication app,
        string projectId,
        string tenantId,
        CancellationToken token)
    {
        var catalog = app.Services.GetRequiredService<IWorkflowCatalogStore>();
        var template = (await catalog.ListTemplatesAsync(tenantId, null, 200, token))
            .Single(item =>
                item.Name == Harness.Host.Workflows.CanonicalWorkflowTemplates
                    .PlaybookStandardTemplate.Name);
        var version = await catalog.GetVersionAsync(
            tenantId, template.CurrentVersionId!, token);
        Assert.NotNull(version);

        var transitions =
            JsonSerializer.Deserialize<Dictionary<string, string[]>>(version.TransitionsJson)!;
        Assert.Equal(
            ["2-Descoberta", "Arquivada", "Roteada-para-Sustentação"],
            transitions["1-Triagem"]);
        Assert.Equal(["9-Sustentação"], transitions["8-Release"]);
        Assert.Empty(transitions["9-Sustentação"]);

        var authority = app.Services.GetRequiredService<IWorkflowStore>();
        var now = DateTimeOffset.UtcNow;
        var runId = UlidValue.New(now).ToString();
        _ = await authority.CreateRunAsync(
            new WorkflowRunCreateCommand(
                tenantId,
                projectId,
                version.Id,
                runId,
                $"playbook-evidence:create:{runId}",
                now),
            token);
        var started = await authority.TransitionRunAsync(
            new WorkflowRunTransitionCommand(
                tenantId,
                runId,
                WorkflowRunTransition.Start,
                1,
                $"playbook-evidence:start:{runId}",
                now.AddTicks(1)),
            token);
        Assert.Equal(WorkflowRunMutationStatus.Applied, started.Status);

        var aggregate = await authority.ReadRunAggregateAsync(tenantId, runId, token);
        Assert.NotNull(aggregate);
        var triage = aggregate.Phases.Single(phase => phase.Name == "1-Triagem");
        var gate = Assert.Single(triage.Gates);
        Assert.Equal(["work-1", "document-1"], gate.RequiredObjectiveKeys);

        var blocked = await authority.EvaluateGateAsync(
            new WorkflowGateEvaluateCommand(
                tenantId,
                runId,
                triage.Key,
                gate.Key,
                Passed: true,
                started.RunVersion!.Value,
                $"playbook-evidence:blocked:{runId}",
                now.AddTicks(2)),
            token);
        Assert.Equal(WorkflowRunMutationStatus.GateRequirementsNotMet, blocked.Status);

        var runVersion = started.RunVersion.Value;
        var tick = 3L;
        foreach (var objectiveKey in new[] { "work-1", "document-1" })
        {
            foreach (var targetState in new[] { "executed", "validated" })
            {
                var advanced = await authority.AdvanceObjectiveAsync(
                    new WorkflowObjectiveAdvanceCommand(
                        tenantId,
                        runId,
                        triage.Key,
                        objectiveKey,
                        targetState,
                        runVersion,
                        $"playbook-evidence:{objectiveKey}:{targetState}:{runId}",
                        now.AddTicks(tick++)),
                    token);
                Assert.Equal(WorkflowRunMutationStatus.Applied, advanced.Status);
                runVersion = advanced.RunVersion!.Value;
            }
        }

        var passed = await authority.EvaluateGateAsync(
            new WorkflowGateEvaluateCommand(
                tenantId,
                runId,
                triage.Key,
                gate.Key,
                Passed: true,
                runVersion,
                $"playbook-evidence:passed:{runId}",
                now.AddTicks(tick)),
            token);
        Assert.Equal(WorkflowRunMutationStatus.Applied, passed.Status);
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
                Key = "PBK",
                Description = "Projeto dos dados do playbook.",
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
