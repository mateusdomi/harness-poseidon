using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Host.Workflows;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Documents.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Modules.Workflows.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Workflows;

public sealed class WorkflowApiTests
{
    [Fact]
    public async Task TemplateBindingAndRunningProjectionSurviveRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"workflow-api-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "workflow.db"); Directory.CreateDirectory(root); var cookies = new CookieContainer();
        string profileId; string projectId; string chiefAgentId; string draftTemplateId; string templateId; string duplicatedTemplateId; string draftVersionId; string publishedV2Id; string workflowId; string runId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token); try
                {
                    var address = Address(app.Services); using (var anonymous = new HttpClient { BaseAddress = address }) using (var denied = await anonymous.GetAsync("/api/v1/workflows", timeout.Token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies }; using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token)) { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }
                    using (var response = await client.PostAsJsonAsync("/api/v1/workflow-templates", new CreateWorkflowTemplateRequest("Rascunho FR-4", "Editável antes da publicação."), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var draft = (await response.Content.ReadFromJsonAsync<WorkflowTemplateContract>(timeout.Token))!;
                        draftTemplateId = draft.Id; Assert.Equal("draft", draft.State);
                        Assert.Null(draft.CurrentVersionId); Assert.Null(draft.ArchivedAt);
                    }
                    var draftVersions = await client.GetFromJsonAsync<WorkflowVersionPage>($"/api/v1/workflow-versions?templateId={draftTemplateId}", timeout.Token);
                    Assert.Empty(draftVersions!.Items);
                    using (var response = await client.PostAsJsonAsync($"/api/v1/workflow-templates/{draftTemplateId}/drafts", new WorkflowDraftRequest(), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var incompleteDraft = (await response.Content.ReadFromJsonAsync<WorkflowVersionContract>(timeout.Token))!;
                        Assert.Equal("draft", incompleteDraft.State); Assert.Empty(incompleteDraft.Phases);
                        using var rejected = await client.PostAsJsonAsync($"/api/v1/workflow-versions/{incompleteDraft.Id}/publish", new PublishWorkflowDraftRequest(), timeout.Token);
                        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
                        using var deletedDraft = await client.DeleteAsync($"/api/v1/workflow-versions/{incompleteDraft.Id}", timeout.Token);
                        Assert.Equal(HttpStatusCode.NoContent, deletedDraft.StatusCode);
                    }
                    using (var deletedTemplate = await client.DeleteAsync($"/api/v1/workflow-templates/{draftTemplateId}", timeout.Token))
                        Assert.Equal(HttpStatusCode.NoContent, deletedTemplate.StatusCode);
                    string organizationId; using (var response = await client.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token)) { response.EnsureSuccessStatusCode(); organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id; }

                    using (var response = await client.PostAsJsonAsync("/api/v1/workflow-templates", new CreateWorkflowTemplateRequest(
                        "Entrega padrão", "Planejar, executar e validar.", ["Planejamento", "Execução"],
                        new Dictionary<string, IReadOnlyList<string>> { { "Execução", ["Qualidade"] } }, "v1"), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, response.StatusCode); var template = await response.Content.ReadFromJsonAsync<WorkflowTemplateContract>(timeout.Token); Assert.NotNull(template); templateId = template.Id; Assert.NotNull(template.CurrentVersionId); }
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest { OrganizationId = organizationId, Name = "Poseidon", Key = "POSEIDON", Description = "Backend", WorkflowTemplateId = templateId }, timeout.Token)) { response.EnsureSuccessStatusCode(); var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!; projectId = project.Id; chiefAgentId = project.ChiefAgentId; }
                    var versions = await client.GetFromJsonAsync<WorkflowVersionPage>($"/api/v1/workflow-versions?templateId={templateId}", timeout.Token);
                    var version = Assert.Single(versions!.Items); Assert.Equal(["Planejamento", "Execução"], version.Phases); Assert.Equal(["Qualidade"], version.GatesByPhase["Execução"]);
                    using (var publish = await client.PostAsJsonAsync($"/api/v1/workflow-templates/{templateId}/versions",
                        new PublishWorkflowVersionRequest(["Planejamento", "Execução", "Publicação"],
                            new Dictionary<string, IReadOnlyList<string>> { ["Execução"] = ["Qualidade"], ["Publicação"] = ["Release"] },
                            new Dictionary<string, WorkflowPhaseConfigContract> { ["Execução"] = new(["technical"], 50m, []) },
                            "semiautonomous", new Dictionary<string, IReadOnlyList<string>> { ["Planejamento"] = ["Execução"], ["Execução"] = ["Publicação"] }, "v2"), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);
                        var v2 = await publish.Content.ReadFromJsonAsync<WorkflowVersionContract>(timeout.Token);
                        publishedV2Id = v2!.Id;
                        Assert.Equal(2, v2?.Version); Assert.Equal("semiautonomous", v2?.DefaultOperationMode);
                        Assert.Equal(50m, v2?.PhaseConfigs["Execução"].ProgressWeight); Assert.Equal("v2", v2?.Changelog);
                    }
                    using (var response = await client.PostAsJsonAsync($"/api/v1/workflow-templates/{templateId}/drafts", new WorkflowDraftRequest(), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var draft = (await response.Content.ReadFromJsonAsync<WorkflowVersionContract>(timeout.Token))!;
                        draftVersionId = draft.Id; Assert.Equal(3, draft.Version);
                        Assert.Equal("draft", draft.State); Assert.Null(draft.PublishedAt);
                        Assert.Equal(["Planejamento", "Execução", "Publicação"], draft.Phases);
                    }
                    using (var response = await client.PatchAsJsonAsync($"/api/v1/workflow-versions/{draftVersionId}", new WorkflowDraftRequest(
                        Phases: ["Planejamento", "Execução", "Publicação", "Homologação"],
                        GatesByPhase: new Dictionary<string, IReadOnlyList<string>> { ["Execução"] = ["Qualidade"], ["Publicação"] = ["Release"] }), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        var edited = (await response.Content.ReadFromJsonAsync<WorkflowVersionContract>(timeout.Token))!;
                        Assert.Equal(["Planejamento", "Execução", "Publicação", "Homologação"], edited.Phases);
                        Assert.Equal("semiautonomous", edited.DefaultOperationMode);
                    }
                    using (var immutable = await client.PatchAsJsonAsync($"/api/v1/workflow-versions/{publishedV2Id}", new WorkflowDraftRequest(Changelog: "não permitido"), timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
                    using (var response = await client.PostAsJsonAsync($"/api/v1/workflow-versions/{draftVersionId}/publish", new PublishWorkflowDraftRequest("v3 homologação"), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        var published = (await response.Content.ReadFromJsonAsync<WorkflowVersionContract>(timeout.Token))!;
                        Assert.Equal("published", published.State); Assert.NotNull(published.PublishedAt);
                        Assert.Equal("v3 homologação", published.Changelog);
                    }
                    var unchangedTemplate = await client.GetFromJsonAsync<WorkflowTemplateContract>($"/api/v1/workflow-templates/{templateId}", timeout.Token);
                    Assert.Equal("published", unchangedTemplate?.State); Assert.Equal(draftVersionId, unchangedTemplate?.CurrentVersionId);
                    string defaultModeProjectId;
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest { OrganizationId = organizationId, Name = "Poseidon Default", Key = "POSEIDON-DEFAULT", Description = "Default workflow mode", WorkflowTemplateId = templateId }, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        defaultModeProjectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!.Id;
                    }
                    {
                        var linked = Assert.Single((await client.GetFromJsonAsync<WorkflowPage>(
                            $"/api/v1/workflows?projectId={defaultModeProjectId}", timeout.Token))!.Items);
                        Assert.Equal(draftVersionId, linked.ActiveVersionId);
                        Assert.Equal("semiautonomous", linked.OperationMode);
                        Assert.Empty(linked.SemiautonomousPauseGates); Assert.Empty(linked.RiskAcceptances);
                    }
                    using (var response = await client.PostAsJsonAsync($"/api/v1/workflow-versions/{publishedV2Id}/archive", new { }, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        var archived = (await response.Content.ReadFromJsonAsync<WorkflowVersionContract>(timeout.Token))!;
                        Assert.Equal("archived", archived.State); Assert.NotNull(archived.ArchivedAt);
                    }
                    using (var currentArchive = await client.PostAsJsonAsync($"/api/v1/workflow-versions/{draftVersionId}/archive", new { }, timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, currentArchive.StatusCode);
                    using (var response = await client.PostAsJsonAsync($"/api/v1/workflow-versions/{draftVersionId}/duplicate", new { }, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var copy = (await response.Content.ReadFromJsonAsync<WorkflowVersionContract>(timeout.Token))!;
                        Assert.Equal(4, copy.Version); Assert.Equal("draft", copy.State);
                        Assert.Equal(["Planejamento", "Execução", "Publicação", "Homologação"], copy.Phases);
                        Assert.Null(copy.Changelog);
                        using var deleted = await client.DeleteAsync($"/api/v1/workflow-versions/{copy.Id}", timeout.Token);
                        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                    }
                    using (var response = await client.PostAsJsonAsync($"/api/v1/workflow-templates/{templateId}/duplicate", new { }, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var copy = (await response.Content.ReadFromJsonAsync<WorkflowTemplateContract>(timeout.Token))!;
                        duplicatedTemplateId = copy.Id; Assert.Equal("Entrega padrão (cópia)", copy.Name);
                        Assert.Equal("draft", copy.State); Assert.Null(copy.CurrentVersionId);
                    }
                    var copiedVersions = await client.GetFromJsonAsync<WorkflowVersionPage>($"/api/v1/workflow-versions?templateId={duplicatedTemplateId}", timeout.Token);
                    var copiedDraft = Assert.Single(copiedVersions!.Items);
                    Assert.Equal(1, copiedDraft.Version); Assert.Equal("draft", copiedDraft.State);
                    Assert.Equal(["Planejamento", "Execução", "Publicação", "Homologação"], copiedDraft.Phases);
                    using (var response = await client.PostAsJsonAsync($"/api/v1/workflow-templates/{duplicatedTemplateId}/archive", new { }, timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        var archived = (await response.Content.ReadFromJsonAsync<WorkflowTemplateContract>(timeout.Token))!;
                        Assert.Equal("archived", archived.State); Assert.NotNull(archived.ArchivedAt);
                    }
                    using (var immutableCopy = await client.PatchAsJsonAsync($"/api/v1/workflow-versions/{copiedDraft.Id}", new WorkflowDraftRequest(Changelog: "bloqueado"), timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, immutableCopy.StatusCode);
                    using (var publishedDelete = await client.DeleteAsync($"/api/v1/workflow-versions/{publishedV2Id}", timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, publishedDelete.StatusCode);
                    using (var templateDelete = await client.DeleteAsync($"/api/v1/workflow-templates/{templateId}", timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, templateDelete.StatusCode);
                    using (var archivedBind = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/workflow", new LinkWorkflowTemplateRequest(templateId, publishedV2Id), timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, archivedBind.StatusCode);
                    {
                        var workflow = Assert.Single((await client.GetFromJsonAsync<WorkflowPage>(
                            $"/api/v1/workflows?projectId={projectId}", timeout.Token))!.Items);
                        workflowId = workflow.Id;
                        Assert.Equal(version.Id, workflow.ActiveVersionId);
                        Assert.Equal("manual", workflow.OperationMode);
                        Assert.Empty(workflow.RiskAcceptances);
                    }
                    using (var duplicate = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/workflow", new LinkWorkflowTemplateRequest(templateId, version.Id), timeout.Token)) Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
                    using (var mode = await client.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/operation-mode",
                        new SetWorkflowOperationModeRequest("semiautonomous", ["Qualidade"], "Pausar no gate crítico."), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.OK, mode.StatusCode); var changed = await mode.Content.ReadFromJsonAsync<WorkflowContract>(timeout.Token);
                        Assert.Equal("semiautonomous", changed?.OperationMode); Assert.Single(changed!.RiskAcceptances);
                    }
                    using (var response = await client.PostAsJsonAsync("/api/v1/workflow-runs", new CreateWorkflowRunRequest(workflowId), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, response.StatusCode); var run = await response.Content.ReadFromJsonAsync<WorkflowRunContract>(timeout.Token); Assert.NotNull(run); runId = run.Id; Assert.Equal("running", run.State); Assert.Null(run.FinishedAt); }
                    var phases = await client.GetFromJsonAsync<PhasePage>($"/api/v1/phases?runId={runId}", timeout.Token); Assert.Equal(2, phases!.Items.Count); Assert.Single(phases.Items, x => x.State == "active");
                    var gates = await client.GetFromJsonAsync<GatePage>($"/api/v1/gates?runId={runId}", timeout.Token); var gate = Assert.Single(gates!.Items); Assert.Equal("Qualidade", gate.Name); Assert.True(gate.RequiresApproval); Assert.Null(gate.DecidedAt);
                    using (var pause = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/transitions", new TransitionWorkflowRunRequest("pause"), timeout.Token)) Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
                    using (var resume = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/transitions", new TransitionWorkflowRunRequest("resume"), timeout.Token)) Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
                    foreach (var state in new[] { "executed", "validated", "approved" })
                        using (var advance = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/objectives", new AdvanceWorkflowObjectiveRequest("phase-1", "work-1", state), timeout.Token)) Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
                    using (var complete = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/phases/phase-1/completion", new { }, timeout.Token)) Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
                    foreach (var state in new[] { "executed", "validated" })
                        using (var advance = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/objectives", new AdvanceWorkflowObjectiveRequest("phase-2", "work-2", state), timeout.Token)) Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
                    using (var missingNote = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/gates", new EvaluateWorkflowGateRequest("phase-2", "gate-1", false), timeout.Token)) Assert.Equal(HttpStatusCode.BadRequest, missingNote.StatusCode);
                    using (var failed = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/gates", new EvaluateWorkflowGateRequest("phase-2", "gate-1", false, "Evidência insuficiente."), timeout.Token)) Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
                    using (var passed = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/gates", new EvaluateWorkflowGateRequest("phase-2", "gate-1", true, "Evidência revisada."), timeout.Token)) Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
                    using (var complete = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{runId}/phases/phase-2/completion", new { }, timeout.Token))
                    { Assert.Equal(HttpStatusCode.OK, complete.StatusCode); var finished = await complete.Content.ReadFromJsonAsync<WorkflowRunContract>(timeout.Token); Assert.Equal("completed", finished?.State); Assert.NotNull(finished?.FinishedAt); }
                    gate = (await client.GetFromJsonAsync<GatePage>($"/api/v1/gates?runId={runId}", timeout.Token))!.Items.Single();
                    Assert.Equal("passed", gate.State); Assert.Equal(profileId, gate.DecidedByProfileId); Assert.Equal("Evidência revisada.", gate.Note);
                    var projectEvents = await WaitForEventsAsync(client, $"project:{projectId}", "gate.changed", 2, timeout.Token);
                    var gatePayload = projectEvents.Delta.Last(x => x.Type == "gate.changed").Payload;
                    Assert.Equal(gate.Id, gatePayload.GetProperty("gateId").GetString()); Assert.Equal(runId, gatePayload.GetProperty("runId").GetString());
                    Assert.Equal("failed", gatePayload.GetProperty("from").GetString()); Assert.Equal("passed", gatePayload.GetProperty("to").GetString());
                    Assert.Equal(profileId, gatePayload.GetProperty("decidedByProfileId").GetString());
                    var globalEvents = await WaitForEventsAsync(client, "global", "workflow.versionPublished", 3, timeout.Token, x => x.Payload.GetProperty("templateId").GetString() == templateId);
                    Assert.Equal([1, 2, 3], globalEvents.Delta.Where(x => x.Type == "workflow.versionPublished" && x.Payload.GetProperty("templateId").GetString() == templateId).Select(x => x.Payload.GetProperty("version").GetInt32()));

                    string approvalRunId; using (var response = await client.PostAsJsonAsync("/api/v1/workflow-runs", new CreateWorkflowRunRequest(workflowId), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, response.StatusCode); approvalRunId = (await response.Content.ReadFromJsonAsync<WorkflowRunContract>(timeout.Token))!.Id; }
                    var approvalGate = Assert.Single((await client.GetFromJsonAsync<GatePage>($"/api/v1/gates?runId={approvalRunId}", timeout.Token))!.Items);
                    string gateApprovalId; using (var requestApproval = await client.PostAsJsonAsync("/api/v1/approvals",
                        new CreateApprovalRequest(projectId, "Liberar gate", "Validação central", chiefAgentId, GateId: approvalGate.Id, Priority: "critical"), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, requestApproval.StatusCode); gateApprovalId = (await requestApproval.Content.ReadFromJsonAsync<ApprovalContract>(timeout.Token))!.Id; }
                    using (var blocked = await client.PostAsJsonAsync($"/api/v1/approvals/{gateApprovalId}/resolution", new ResolveApprovalRequest("approved"), timeout.Token)) Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                    foreach (var state in new[] { "executed", "validated", "approved" }) using (var advance = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{approvalRunId}/objectives", new AdvanceWorkflowObjectiveRequest("phase-1", "work-1", state), timeout.Token)) Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
                    using (var complete = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{approvalRunId}/phases/phase-1/completion", new { }, timeout.Token)) Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
                    foreach (var state in new[] { "executed", "validated" }) using (var advance = await client.PostAsJsonAsync($"/api/v1/workflow-runs/{approvalRunId}/objectives", new AdvanceWorkflowObjectiveRequest("phase-2", "work-2", state), timeout.Token)) Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
                    using (var approved = await client.PostAsJsonAsync($"/api/v1/approvals/{gateApprovalId}/resolution", new ResolveApprovalRequest("approved", "Requisitos atendidos."), timeout.Token)) Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
                    approvalGate = Assert.Single((await client.GetFromJsonAsync<GatePage>($"/api/v1/gates?runId={approvalRunId}", timeout.Token))!.Items);
                    Assert.Equal("passed", approvalGate.State); Assert.Equal(profileId, approvalGate.DecidedByProfileId);
                    var approvalGateEvents = await WaitForEventsAsync(client, $"project:{projectId}", "gate.changed", 3, timeout.Token);
                    Assert.Equal("approved", approvalGateEvents.Delta.Last(x => x.Type == "gate.changed").Payload.GetProperty("to").GetString());
                }
                finally { await app.StopAsync(timeout.Token); }
            }
            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token); try
            { using var client = new HttpClient { BaseAddress = Address(restarted.Services) }; client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}"); using var deletedDraftTemplate = await client.GetAsync($"/api/v1/workflow-templates/{draftTemplateId}", timeout.Token); Assert.Equal(HttpStatusCode.NotFound, deletedDraftTemplate.StatusCode); var archivedCopy = await client.GetFromJsonAsync<WorkflowTemplateContract>($"/api/v1/workflow-templates/{duplicatedTemplateId}", timeout.Token); Assert.Equal("archived", archivedCopy?.State); Assert.NotNull(archivedCopy?.ArchivedAt); var draftVersion = await client.GetFromJsonAsync<WorkflowVersionContract>($"/api/v1/workflow-versions/{draftVersionId}", timeout.Token); Assert.Equal("published", draftVersion?.State); Assert.NotNull(draftVersion?.PublishedAt); var workflow = await client.GetFromJsonAsync<WorkflowContract>($"/api/v1/workflows/{workflowId}", timeout.Token); Assert.Equal(projectId, workflow?.ProjectId); var run = await client.GetFromJsonAsync<WorkflowRunContract>($"/api/v1/workflow-runs/{runId}", timeout.Token); Assert.Equal("completed", run?.State); }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static WebApplication CreateHost(string database) => HostApplication.Build(["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static async Task<EventStreamSnapshot> WaitForEventsAsync(HttpClient client, string stream, string type, int count, CancellationToken token, Func<RealtimeEventEnvelope, bool>? filter = null)
    {
        for (var index = 0; index < 200; index++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>($"/api/v1/event-streams/snapshot?stream={Uri.EscapeDataString(stream)}", token);
            if (snapshot is not null && snapshot.Delta.Count(x => x.Type == type && (filter is null || filter(x))) >= count) return snapshot;
            await Task.Delay(25, token);
        }
        throw new TimeoutException($"Events {type} were not dispatched to {stream}.");
    }
    private static Uri Address(IServiceProvider services) { var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address."); return new Uri(addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))); }
}
