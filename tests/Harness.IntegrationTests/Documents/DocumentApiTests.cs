using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Documents;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Documents.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Documents;

public sealed class DocumentApiTests
{
    [Fact]
    public async Task DocumentsAndImmutableBodiesSurviveHostRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"document-api-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "document.db"); var catalog = Path.Combine(root, "catalog");
        Directory.CreateDirectory(root); var cookies = new CookieContainer();
        string profileId; string projectId; string chiefAgentId; string documentId; string firstVersionId; string secondVersionId; string thirdVersionId; string fourthVersionId; string approvedApprovalId; string humanApprovalId;
        try
        {
            await using (var app = CreateHost(database, catalog))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.GetAsync("/api/v1/documents", timeout.Token))
                        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles",
                        new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                    { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }
                    string organizationId;
                    using (var response = await client.PostAsJsonAsync("/api/v1/organizations",
                        new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token))
                    { response.EnsureSuccessStatusCode(); organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id; }
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects",
                        new CreateProjectRequest { OrganizationId = organizationId, Name = "Poseidon", Key = "POSEIDON", Description = "Backend" }, timeout.Token))
                    { response.EnsureSuccessStatusCode(); var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!; projectId = project.Id; chiefAgentId = project.ChiefAgentId; }

                    using (var response = await client.PostAsJsonAsync("/api/v1/documents",
                        new CreateDocumentRequest(projectId, "ADR 001", "spec", "# v1", ["ux", "arquitetura"], null), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var document = await response.Content.ReadFromJsonAsync<DocumentContract>(timeout.Token);
                        Assert.NotNull(document); documentId = document.Id;
                        Assert.Equal("inElaboration", document.State); Assert.Equal(["arquitetura", "ux"], document.Classifications);
                        Assert.Null(document.PhaseName); Assert.Null(document.Waiver); Assert.Equal(1, document.CurrentVersion);
                    }
                    var page = await client.GetFromJsonAsync<DocumentPage>($"/api/v1/documents?projectId={projectId}", timeout.Token);
                    Assert.Equal(documentId, Assert.Single(page!.Items).Id);
                    Assert.Equal(1, page.Total); Assert.Equal(1, page.Page);
                    Assert.Equal(15, page.PageSize); Assert.Null(page.NextCursor);
                    var filteredPage = await client.GetFromJsonAsync<DocumentPage>(
                        $"/api/v1/documents?projectId={projectId}&q=adr&kind=spec&state=inElaboration" +
                        "&classification=arquitetura&orphan=true&page=1&pageSize=1", timeout.Token);
                    Assert.Equal(documentId, Assert.Single(filteredPage!.Items).Id);
                    Assert.Equal(1, filteredPage.Total); Assert.Equal(1, filteredPage.PageSize);
                    using (var invalidPage = await client.GetAsync(
                        $"/api/v1/documents?projectId={projectId}&pageSize=0", timeout.Token))
                        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
                    using (var mixedPage = await client.GetAsync(
                        $"/api/v1/documents?projectId={projectId}&cursor={documentId}&page=1", timeout.Token))
                        Assert.Equal(HttpStatusCode.BadRequest, mixedPage.StatusCode);
                    var versions = await client.GetFromJsonAsync<DocumentVersionPage>($"/api/v1/document-versions?documentId={documentId}", timeout.Token);
                    var first = Assert.Single(versions!.Items); firstVersionId = first.Id;
                    Assert.Equal("# v1", first.Body); Assert.Equal("user", first.AuthorKind); Assert.Equal(profileId, first.AuthorId);

                    using (var response = await client.PostAsJsonAsync("/api/v1/document-versions",
                        new CreateDocumentVersionRequest(documentId, "# v2\n\nCorrigido."), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var version = await response.Content.ReadFromJsonAsync<DocumentVersionContract>(timeout.Token);
                        Assert.NotNull(version); secondVersionId = version.Id; Assert.Equal(2, version.Version); Assert.Equal("# v2\n\nCorrigido.", version.Body);
                    }
                    versions = await client.GetFromJsonAsync<DocumentVersionPage>($"/api/v1/document-versions?documentId={documentId}", timeout.Token);
                    Assert.Equal(["# v1", "# v2\n\nCorrigido."], versions!.Items.OrderBy(value => value.Version).Select(value => value.Body));
                    Assert.Equal(2, (await client.GetFromJsonAsync<DocumentContract>($"/api/v1/documents/{documentId}", timeout.Token))!.CurrentVersion);

                    using (var classify = await client.PostAsJsonAsync($"/api/v1/documents/{documentId}/classification",
                        new ClassifyDocumentRequest(["normativo", "arquitetura"], "Revisão"), timeout.Token))
                    { Assert.Equal(HttpStatusCode.OK, classify.StatusCode); var changed = await classify.Content.ReadFromJsonAsync<DocumentContract>(timeout.Token); Assert.Equal(["arquitetura", "normativo"], changed?.Classifications); Assert.Equal("Revisão", changed?.PhaseName); }
                    var phasePage = await client.GetFromJsonAsync<DocumentPage>(
                        $"/api/v1/documents?projectId={projectId}&phaseName={Uri.EscapeDataString("Revisão")}",
                        timeout.Token);
                    Assert.Equal(documentId, Assert.Single(phasePage!.Items).Id);
                    using (var review = await client.PostAsJsonAsync($"/api/v1/documents/{documentId}/transitions",
                        new TransitionDocumentRequest("inReview"), timeout.Token))
                    { Assert.Equal(HttpStatusCode.OK, review.StatusCode); Assert.Equal("inReview", (await review.Content.ReadFromJsonAsync<DocumentContract>(timeout.Token))?.State); }
                    using (var manualReviewEdit = await client.PostAsJsonAsync(
                        $"/api/v1/documents/{documentId}/versions",
                        new SaveDocumentVersionRequest("# v3\n\nEditado durante a revisão."), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, manualReviewEdit.StatusCode);
                        var version = await manualReviewEdit.Content.ReadFromJsonAsync<DocumentVersionContract>(timeout.Token);
                        Assert.NotNull(version); thirdVersionId = version.Id; Assert.Equal(3, version.Version);
                        Assert.Equal("user", version.AuthorKind); Assert.Equal(profileId, version.AuthorId);
                    }
                    using (var intent = await client.PostAsJsonAsync($"/api/v1/documents/{documentId}/transitions",
                        new TransitionDocumentRequest("awaitingApproval"), timeout.Token))
                    { Assert.Equal(HttpStatusCode.OK, intent.StatusCode); Assert.Equal("inReview", (await intent.Content.ReadFromJsonAsync<DocumentContract>(timeout.Token))?.State); }
                    string rejectedApprovalId;
                    using (var requestApproval = await client.PostAsJsonAsync("/api/v1/approvals",
                        new CreateApprovalRequest(projectId, "Aprovar ADR", "Revisão humana", chiefAgentId,
                            DocumentId: documentId), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, requestApproval.StatusCode); var approval = await requestApproval.Content.ReadFromJsonAsync<ApprovalContract>(timeout.Token); Assert.NotNull(approval); rejectedApprovalId = approval.Id; Assert.Equal("pending", approval.State); Assert.Equal("medium", approval.Priority); }
                    Assert.Equal("awaitingApproval", (await client.GetFromJsonAsync<DocumentContract>($"/api/v1/documents/{documentId}", timeout.Token))!.State);
                    using (var manualApprovalEdit = await client.PostAsJsonAsync(
                        $"/api/v1/documents/{documentId}/versions",
                        new SaveDocumentVersionRequest("# v4\n\nAjuste final do aprovador."), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, manualApprovalEdit.StatusCode);
                        var version = await manualApprovalEdit.Content.ReadFromJsonAsync<DocumentVersionContract>(timeout.Token);
                        Assert.NotNull(version); fourthVersionId = version.Id; Assert.Equal(4, version.Version);
                    }
                    using (var missingNote = await client.PostAsJsonAsync($"/api/v1/approvals/{rejectedApprovalId}/resolution",
                        new ResolveApprovalRequest("rejected"), timeout.Token)) Assert.Equal(HttpStatusCode.BadRequest, missingNote.StatusCode);
                    using (var reject = await client.PostAsJsonAsync($"/api/v1/approvals/{rejectedApprovalId}/resolution",
                        new ResolveApprovalRequest("rejected", "Ajustar decisão."), timeout.Token))
                    { Assert.Equal(HttpStatusCode.OK, reject.StatusCode); var approval = await reject.Content.ReadFromJsonAsync<ApprovalContract>(timeout.Token); Assert.Equal("rejected", approval?.State); Assert.Equal(profileId, approval?.ResolvedByProfileId); }
                    Assert.Equal("inElaboration", (await client.GetFromJsonAsync<DocumentContract>($"/api/v1/documents/{documentId}", timeout.Token))!.State);
                    using (var review = await client.PostAsJsonAsync($"/api/v1/documents/{documentId}/transitions",
                        new TransitionDocumentRequest("inReview", "Correção revisada."), timeout.Token)) Assert.Equal(HttpStatusCode.OK, review.StatusCode);
                    using (var requestApproval = await client.PostAsJsonAsync("/api/v1/approvals",
                        new CreateApprovalRequest(projectId, "Aprovar ADR corrigido", "Segunda revisão", chiefAgentId,
                            DocumentId: documentId, Priority: "high"), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, requestApproval.StatusCode); approvedApprovalId = (await requestApproval.Content.ReadFromJsonAsync<ApprovalContract>(timeout.Token))!.Id; }
                    using (var approve = await client.PostAsJsonAsync($"/api/v1/approvals/{approvedApprovalId}/resolution",
                        new ResolveApprovalRequest("approved", "Aceito."), timeout.Token)) Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
                    var approvals = await client.GetFromJsonAsync<ApprovalPage>($"/api/v1/approvals?projectId={projectId}", timeout.Token);
                    Assert.Equal(["rejected", "approved"], approvals!.Items.OrderBy(value => value.RequestedAt).Select(value => value.State));
                    Assert.Equal("approved", (await client.GetFromJsonAsync<DocumentContract>($"/api/v1/documents/{documentId}", timeout.Token))!.State);
                    using (var editApproved = await client.PostAsJsonAsync(
                        $"/api/v1/documents/{documentId}/versions",
                        new SaveDocumentVersionRequest("não pode editar aprovado"), timeout.Token))
                        Assert.Equal(HttpStatusCode.Conflict, editApproved.StatusCode);
                    var requestedEvents = await WaitForEventsAsync(client, $"project:{projectId}", "approval.requested", 2, timeout.Token);
                    var requested = requestedEvents.Delta.Last(value => value.Type == "approval.requested").Payload.GetProperty("approval");
                    Assert.Equal(approvedApprovalId, requested.GetProperty("id").GetString()); Assert.Equal(documentId, requested.GetProperty("documentId").GetString());
                    var resolvedEvents = await WaitForEventsAsync(client, $"project:{projectId}", "approval.resolved", 2, timeout.Token);
                    var resolved = resolvedEvents.Delta.Last(value => value.Type == "approval.resolved").Payload;
                    Assert.Equal(approvedApprovalId, resolved.GetProperty("approvalId").GetString()); Assert.Equal("approved", resolved.GetProperty("state").GetString()); Assert.Equal(profileId, resolved.GetProperty("resolvedByProfileId").GetString());
                    var documentEvents = await WaitForEventsAsync(client, $"project:{projectId}", "document.stateChanged", 11, timeout.Token);
                    var stateChanged = documentEvents.Delta.Last(value => value.Type == "document.stateChanged").Payload;
                    Assert.Equal(documentId, stateChanged.GetProperty("documentId").GetString()); Assert.Equal("awaitingApproval", stateChanged.GetProperty("from").GetString()); Assert.Equal("approved", stateChanged.GetProperty("to").GetString());

                    string taskId;
                    using (var createTask = await client.PostAsJsonAsync("/api/v1/tasks",
                        new CreateTaskRequest(projectId, "Revisar entrega", "Valide as evidências."), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, createTask.StatusCode); taskId = (await createTask.Content.ReadFromJsonAsync<BoardTaskContract>(timeout.Token))!.Id; }
                    string taskApprovalId;
                    using (var requestTask = await client.PostAsJsonAsync("/api/v1/approvals",
                        new CreateApprovalRequest(projectId, "Aceitar tarefa", "Validação humana", chiefAgentId,
                            TaskId: taskId, Priority: "critical"), timeout.Token))
                    { Assert.True(requestTask.StatusCode == HttpStatusCode.Created, await requestTask.Content.ReadAsStringAsync(timeout.Token)); var approval = (await requestTask.Content.ReadFromJsonAsync<ApprovalContract>(timeout.Token))!; taskApprovalId = approval.Id; Assert.Equal(taskId, approval.TaskId); }
                    using (var approveTask = await client.PostAsJsonAsync($"/api/v1/approvals/{taskApprovalId}/resolution",
                        new ResolveApprovalRequest("approved"), timeout.Token)) Assert.Equal(HttpStatusCode.OK, approveTask.StatusCode);
                    using (var requestHuman = await client.PostAsJsonAsync("/api/v1/approvals",
                        new CreateApprovalRequest(projectId, "Decisão de produto", "Escolher estratégia", chiefAgentId,
                            DueAt: DateTimeOffset.UtcNow.AddDays(1)), timeout.Token))
                    { Assert.Equal(HttpStatusCode.Created, requestHuman.StatusCode); var approval = (await requestHuman.Content.ReadFromJsonAsync<ApprovalContract>(timeout.Token))!; humanApprovalId = approval.Id; Assert.Null(approval.TaskId); Assert.Null(approval.GateId); Assert.Null(approval.DocumentId); }
                    using (var rejectHuman = await client.PostAsJsonAsync($"/api/v1/approvals/{humanApprovalId}/resolution",
                        new ResolveApprovalRequest("rejected", "Estratégia recusada."), timeout.Token)) Assert.Equal(HttpStatusCode.OK, rejectHuman.StatusCode);
                    approvals = await client.GetFromJsonAsync<ApprovalPage>($"/api/v1/approvals?projectId={projectId}", timeout.Token);
                    Assert.Equal(4, approvals!.Items.Count); Assert.Equal("critical", approvals.Items.Single(value => value.Id == taskApprovalId).Priority);
                    var allResolved = await WaitForEventsAsync(client, $"project:{projectId}", "approval.resolved", 4, timeout.Token);
                    Assert.Equal(humanApprovalId, allResolved.Delta.Last(value => value.Type == "approval.resolved").Payload.GetProperty("approvalId").GetString());
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await using var restarted = CreateHost(database, catalog); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                Assert.Equal("# v1", (await client.GetFromJsonAsync<DocumentVersionContract>($"/api/v1/document-versions/{firstVersionId}", timeout.Token))!.Body);
                Assert.Equal("# v2\n\nCorrigido.", (await client.GetFromJsonAsync<DocumentVersionContract>($"/api/v1/document-versions/{secondVersionId}", timeout.Token))!.Body);
                Assert.Equal("# v3\n\nEditado durante a revisão.", (await client.GetFromJsonAsync<DocumentVersionContract>($"/api/v1/document-versions/{thirdVersionId}", timeout.Token))!.Body);
                Assert.Equal("# v4\n\nAjuste final do aprovador.", (await client.GetFromJsonAsync<DocumentVersionContract>($"/api/v1/document-versions/{fourthVersionId}", timeout.Token))!.Body);
                var document = await client.GetFromJsonAsync<DocumentContract>($"/api/v1/documents/{documentId}", timeout.Token);
                Assert.Equal(4, document!.CurrentVersion); Assert.Equal("approved", document.State);
                Assert.Equal("approved", (await client.GetFromJsonAsync<ApprovalContract>($"/api/v1/approvals/{approvedApprovalId}", timeout.Token))!.State);
                Assert.Equal("rejected", (await client.GetFromJsonAsync<ApprovalContract>($"/api/v1/approvals/{humanApprovalId}", timeout.Token))!.State);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static WebApplication CreateHost(string database, string catalog) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database,
         "--Harness:DocumentCatalogPath", catalog]);
    private static async Task<EventStreamSnapshot> WaitForEventsAsync(
        HttpClient client, string stream, string type, int count, CancellationToken token)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                $"/api/v1/event-streams/snapshot?stream={Uri.EscapeDataString(stream)}", token);
            if (snapshot is not null && snapshot.Delta.Count(value => value.Type == type) >= count) return snapshot;
            await Task.Delay(25, token);
        }
        throw new TimeoutException($"Events {type} were not dispatched.");
    }
    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
