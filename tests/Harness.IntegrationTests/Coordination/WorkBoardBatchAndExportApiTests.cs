using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Coordination;

public sealed class WorkBoardBatchAndExportApiTests
{
    [Fact]
    public async Task BatchMoveAppliesToManyReportsTypedFailuresAndIsolatesTenants()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"batch-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "board.db");
        var otherDatabase = Path.Combine(root, "board-other.db"); Directory.CreateDirectory(root);
        try
        {
            await using var app = CreateHost(database);
            // A second Poseidon tenant (own database + profile), to prove cross-tenant ids are rejected.
            await using var otherApp = CreateHost(otherDatabase);
            await app.StartAsync(timeout.Token);
            await otherApp.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = address };
                var projectId = await BootstrapProjectAsync(client, timeout.Token);
                var taskA = await CreateTaskAsync(client, projectId, "Task A", timeout.Token);
                var taskB = await CreateTaskAsync(client, projectId, "Task B", timeout.Token);
                var taskC = await CreateTaskAsync(client, projectId, "Task C", timeout.Token);

                using var otherHandler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var otherClient = new HttpClient(otherHandler) { BaseAddress = Address(otherApp.Services) };
                var otherProjectId = await BootstrapProjectAsync(otherClient, timeout.Token);
                var foreignTask = await CreateTaskAsync(otherClient, otherProjectId, "Foreign", timeout.Token);

                // Bulk move: three of ours + a nonexistent ULID + the foreign task + a malformed id.
                var missing = UlidValue.New(DateTimeOffset.UtcNow).ToString();
                using var moveResponse = await client.PostAsJsonAsync("/api/v1/tasks/batch",
                    new BatchTaskOperationRequest("move",
                        [taskA, taskB, taskC, missing, foreignTask, "not-a-ulid"], ToState: "ready"),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
                var move = (await moveResponse.Content.ReadFromJsonAsync<BatchTaskOperationResult>(timeout.Token))!;
                Assert.Equal("move", move.Operation);
                Assert.Equal(6, move.Total); Assert.Equal(3, move.Succeeded); Assert.Equal(3, move.Failed);
                Assert.All([taskA, taskB, taskC], id =>
                    Assert.Equal("succeeded", Reason(move, id).Status));
                Assert.Equal("task_not_found", Reason(move, missing).Reason);
                Assert.Equal("task_not_found", Reason(move, foreignTask).Reason);
                Assert.Equal("invalid_task_id", Reason(move, "not-a-ulid").Reason);

                // The successful items really moved; the foreign task was untouched in its tenant.
                foreach (var id in new[] { taskA, taskB, taskC })
                {
                    var moved = await client.GetFromJsonAsync<BoardTaskContract>($"/api/v1/tasks/{id}", timeout.Token);
                    Assert.Equal("ready", moved?.State);
                }
                var untouched = await otherClient.GetFromJsonAsync<BoardTaskContract>(
                    $"/api/v1/tasks/{foreignTask}", timeout.Token);
                Assert.Equal("backlog", untouched?.State);

                // Bulk priority applies to many.
                using var priorityResponse = await client.PostAsJsonAsync("/api/v1/tasks/batch",
                    new BatchTaskOperationRequest("priority", [taskA, taskB], Priority: "critical"), timeout.Token);
                var priority = (await priorityResponse.Content.ReadFromJsonAsync<BatchTaskOperationResult>(timeout.Token))!;
                Assert.Equal(2, priority.Succeeded); Assert.Equal(0, priority.Failed);
                Assert.Equal("critical",
                    (await client.GetFromJsonAsync<BoardTaskContract>($"/api/v1/tasks/{taskA}", timeout.Token))?.Priority);

                // Guards are honored per item: ->done requires internal completion, so all conflict.
                using var doneResponse = await client.PostAsJsonAsync("/api/v1/tasks/batch",
                    new BatchTaskOperationRequest("move", [taskA, taskB], ToState: "done"), timeout.Token);
                var done = (await doneResponse.Content.ReadFromJsonAsync<BatchTaskOperationResult>(timeout.Token))!;
                Assert.Equal(0, done.Succeeded); Assert.Equal(2, done.Failed);
                Assert.All(done.Results, r => Assert.Equal("state_conflict", r.Reason));

                // Operation-level bad args are a 400 for the whole request (not per item).
                using var badOperation = await client.PostAsJsonAsync("/api/v1/tasks/batch",
                    new BatchTaskOperationRequest("frobnicate", [taskA]), timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, badOperation.StatusCode);
                using var emptyIds = await client.PostAsJsonAsync("/api/v1/tasks/batch",
                    new BatchTaskOperationRequest("move", [], ToState: "ready"), timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, emptyIds.StatusCode);
            }
            finally { await otherApp.StopAsync(timeout.Token); await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CsvExportHonorsFiltersEscapesFieldsAndIsExcelCompatible()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"csv-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "board.db"); Directory.CreateDirectory(root);
        try
        {
            await using var app = CreateHost(database);
            await app.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = address };
                var projectId = await BootstrapProjectAsync(client, timeout.Token);

                // A tricky title: comma, embedded quotes, and a newline must all be CSV-escaped.
                const string trickyTitle = "He said \"hi\", bye\nend";
                var tricky = await CreateTaskAsync(client, projectId, trickyTitle, timeout.Token, phaseName: "Alpha");
                var plain = await CreateTaskAsync(client, projectId, "Plain task", timeout.Token, phaseName: "Beta");

                using var response = await client.GetAsync($"/api/v1/tasks/export.csv?projectId={projectId}", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
                Assert.Equal("board-tasks.csv", response.Content.Headers.ContentDisposition?.FileName);

                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                // UTF-8 BOM.
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
                var text = Encoding.UTF8.GetString(bytes[3..]);
                // CRLF line endings and header shape.
                Assert.StartsWith(
                    "id,title,state,priority,cardType,demandId,assigneeAgentId,phaseName,createdAt,updatedAt,dueAt,archivedAt\r\n",
                    text, StringComparison.Ordinal);
                Assert.Contains("\r\n", text, StringComparison.Ordinal);
                Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
                // The tricky title is quoted with doubled inner quotes; comma/newline stay inside the quotes.
                Assert.Contains("\"He said \"\"hi\"\", bye\nend\"", text, StringComparison.Ordinal);
                Assert.Contains(tricky, text, StringComparison.Ordinal);
                Assert.Contains(plain, text, StringComparison.Ordinal);

                // Filters are honored server-side: phaseName=Beta yields only the plain task row.
                using var filtered = await client.GetAsync(
                    $"/api/v1/tasks/export.csv?projectId={projectId}&phaseName=Beta", timeout.Token);
                var filteredText = Encoding.UTF8.GetString((await filtered.Content.ReadAsByteArrayAsync(timeout.Token))[3..]);
                Assert.Contains(plain, filteredText, StringComparison.Ordinal);
                Assert.DoesNotContain(tricky, filteredText, StringComparison.Ordinal);
                // Header plus exactly one data row.
                var dataRows = filteredText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, dataRows.Length);
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static BatchTaskItemResult Reason(BatchTaskOperationResult result, string taskId) =>
        result.Results.Single(r => r.TaskId == taskId);

    private static async Task<string> BootstrapProjectAsync(HttpClient client, CancellationToken token)
    {
        using (var profile = await client.PostAsJsonAsync("/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"), token))
            profile.EnsureSuccessStatusCode();
        var organization = (await (await client.PostAsJsonAsync("/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token))
            .Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
        var project = (await (await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest
        { OrganizationId = organization.Id, Name = "Poseidon", Key = "POSEIDON", Description = "Backend" }, token))
            .Content.ReadFromJsonAsync<ProjectResponse>(token))!;
        return project.Id;
    }

    private static async Task<string> CreateTaskAsync(
        HttpClient client, string projectId, string title, CancellationToken token, string? phaseName = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/tasks",
            new CreateTaskRequest(projectId, title, "Implemente e valide", PhaseName: phaseName), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardTaskContract>(token))!.Id;
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
