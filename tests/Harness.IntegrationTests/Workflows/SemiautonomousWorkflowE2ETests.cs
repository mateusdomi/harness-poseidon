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

public sealed class SemiautonomousWorkflowE2ETests
{
    [Fact]
    public async Task CanonicalRunCompletesInSemiautonomousModeAndGatesResistBypassInEveryMode()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"semiautonomous-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "semiautonomous.db");
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
                await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                var templates = (await client.GetFromJsonAsync<WorkflowTemplatePage>(
                    "/api/v1/workflow-templates?limit=50", timeout.Token))!;
                var standard = templates.Items.Single(template =>
                    template.Name.StartsWith("Entrega padrão", StringComparison.Ordinal));
                var version = (await client.GetFromJsonAsync<WorkflowVersionContract>(
                    $"/api/v1/workflow-versions/{standard.CurrentVersionId}", timeout.Token))!;

                using var bindingResponse = await client.PostAsJsonAsync(
                    "/api/v1/workflows",
                    new CreateWorkflowRequest(
                        project.Id,
                        standard.Id,
                        null,
                        "semiautonomous",
                        ["Aprovação de Homologação"],
                        "Aceite de risco para o fluxo semiautônomo canônico."),
                    timeout.Token);
                bindingResponse.EnsureSuccessStatusCode();
                var workflow = (await bindingResponse.Content
                    .ReadFromJsonAsync<WorkflowContract>(timeout.Token))!;
                Assert.Equal("semiautonomous", workflow.OperationMode);
                Assert.Equal(["Aprovação de Homologação"], workflow.SemiautonomousPauseGates);

                using var runResponse = await client.PostAsJsonAsync(
                    "/api/v1/workflow-runs",
                    new CreateWorkflowRunRequest(workflow.Id),
                    timeout.Token);
                runResponse.EnsureSuccessStatusCode();
                var run = (await runResponse.Content
                    .ReadFromJsonAsync<WorkflowRunContract>(timeout.Token))!;

                // Burlas no fluxo: fase não conclui com objetivo pendente, gate não avalia
                // sem requisito mínimo e reprovação de gate exige nota.
                using (var bypassPhase = await client.PostAsJsonAsync(
                    $"/api/v1/workflow-runs/{run.Id}/phases/phase-1/completion",
                    new { },
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Conflict, bypassPhase.StatusCode);
                }

                var phaseCount = version.Phases.Count;
                var gatedPhaseKeys = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < phaseCount; index++)
                {
                    if (version.GatesByPhase.ContainsKey(version.Phases[index]))
                    {
                        gatedPhaseKeys.Add($"phase-{index + 1}");
                    }
                }

                for (var index = 1; index <= phaseCount; index++)
                {
                    var phaseKey = $"phase-{index}";
                    var workKey = $"work-{index}";
                    var gated = gatedPhaseKeys.Contains(phaseKey);
                    if (gated && index == 2)
                    {
                        using var earlyGate = await client.PostAsJsonAsync(
                            $"/api/v1/workflow-runs/{run.Id}/gates",
                            new EvaluateWorkflowGateRequest(phaseKey, "gate-1", true, null),
                            timeout.Token);
                        Assert.Equal(HttpStatusCode.Conflict, earlyGate.StatusCode);
                    }

                    foreach (var state in (string[])["executed", "validated", "approved"])
                    {
                        using var advance = await client.PostAsJsonAsync(
                            $"/api/v1/workflow-runs/{run.Id}/objectives",
                            new AdvanceWorkflowObjectiveRequest(phaseKey, workKey, state),
                            timeout.Token);
                        Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
                    }

                    if (gated)
                    {
                        if (index == 2)
                        {
                            using var noteless = await client.PostAsJsonAsync(
                                $"/api/v1/workflow-runs/{run.Id}/gates",
                                new EvaluateWorkflowGateRequest(phaseKey, "gate-1", false, null),
                                timeout.Token);
                            Assert.Equal(HttpStatusCode.BadRequest, noteless.StatusCode);
                            using var failed = await client.PostAsJsonAsync(
                                $"/api/v1/workflow-runs/{run.Id}/gates",
                                new EvaluateWorkflowGateRequest(
                                    phaseKey,
                                    "gate-1",
                                    false,
                                    "Evidência insuficiente para o gate."),
                                timeout.Token);
                            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
                        }

                        using var passed = await client.PostAsJsonAsync(
                            $"/api/v1/workflow-runs/{run.Id}/gates",
                            new EvaluateWorkflowGateRequest(
                                phaseKey,
                                "gate-1",
                                true,
                                "Evidência revisada e aprovada."),
                            timeout.Token);
                        Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
                    }

                    using var completion = await client.PostAsJsonAsync(
                        $"/api/v1/workflow-runs/{run.Id}/phases/{phaseKey}/completion",
                        new { },
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
                }

                var finished = (await client.GetFromJsonAsync<WorkflowRunContract>(
                    $"/api/v1/workflow-runs/{run.Id}", timeout.Token))!;
                Assert.Equal("completed", finished.State);
                Assert.NotNull(finished.FinishedAt);

                using var consistencyResponse = await client.PostAsJsonAsync(
                    $"/api/v1/workflow-runs/{run.Id}/consistency-checks",
                    new { },
                    timeout.Token);
                consistencyResponse.EnsureSuccessStatusCode();
                var consistency = (await consistencyResponse.Content
                    .ReadFromJsonAsync<WorkflowConsistencyCheckResponse>(timeout.Token))!;
                Assert.True(consistency.Consistent);
                Assert.All(consistency.Layers, layer => Assert.Equal("passed", layer.State));

                // Troca para autônomo exige novo aceite de risco e os gates continuam
                // não-contornáveis no novo modo.
                using var modeResponse = await client.PostAsJsonAsync(
                    $"/api/v1/workflows/{workflow.Id}/operation-mode",
                    new SetWorkflowOperationModeRequest(
                        "autonomous",
                        null,
                        "Aceite de risco para operação autônoma supervisionada."),
                    timeout.Token);
                modeResponse.EnsureSuccessStatusCode();
                var autonomous = (await modeResponse.Content
                    .ReadFromJsonAsync<WorkflowContract>(timeout.Token))!;
                Assert.Equal("autonomous", autonomous.OperationMode);
                Assert.Equal(2, autonomous.RiskAcceptances.Count);
                Assert.Equal(
                    ["semiautonomous", "autonomous"],
                    autonomous.RiskAcceptances.Select(acceptance => acceptance.Mode));

                using var secondRunResponse = await client.PostAsJsonAsync(
                    "/api/v1/workflow-runs",
                    new CreateWorkflowRunRequest(workflow.Id),
                    timeout.Token);
                secondRunResponse.EnsureSuccessStatusCode();
                var secondRun = (await secondRunResponse.Content
                    .ReadFromJsonAsync<WorkflowRunContract>(timeout.Token))!;
                foreach (var state in (string[])["executed", "validated", "approved"])
                {
                    using var advance = await client.PostAsJsonAsync(
                        $"/api/v1/workflow-runs/{secondRun.Id}/objectives",
                        new AdvanceWorkflowObjectiveRequest("phase-1", "work-1", state),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
                }

                using (var completePhase1 = await client.PostAsJsonAsync(
                    $"/api/v1/workflow-runs/{secondRun.Id}/phases/phase-1/completion",
                    new { },
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, completePhase1.StatusCode);
                }

                foreach (var state in (string[])["executed", "validated", "approved"])
                {
                    using var advance = await client.PostAsJsonAsync(
                        $"/api/v1/workflow-runs/{secondRun.Id}/objectives",
                        new AdvanceWorkflowObjectiveRequest("phase-2", "work-2", state),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
                }

                using var autonomousBypass = await client.PostAsJsonAsync(
                    $"/api/v1/workflow-runs/{secondRun.Id}/phases/phase-2/completion",
                    new { },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Conflict, autonomousBypass.StatusCode);
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
}
