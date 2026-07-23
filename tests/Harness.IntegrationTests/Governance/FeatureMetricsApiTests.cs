using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Governance;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Governance;

// PLAT-04: prova end-to-end (SQLite in-process) da camada de medição — métricas por feature,
// detecção de travamento semântico e o juiz determinístico — sobre tentativas gravadas com
// desfechos/custos conhecidos.
public sealed class FeatureMetricsApiTests
{
    [Fact]
    public async Task FeatureMetricsStuckDetectionAndJudgeDeriveFromRecordedAttempts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"feature-metrics-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "metrics.db");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                var profile = await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                    .GetAsync(profile.Id, timeout.Token))!.TenantId;

                var catBuild = await CreateTaskAsync(client, project.Id, "[CAT-04] Build metrics read-model", timeout.Token);
                var catWire = await CreateTaskAsync(client, project.Id, "CAT-04/T02 Wire endpoint", timeout.Token);
                var platStuck = await CreateTaskAsync(client, project.Id, "[PLAT-04] Semantic stuck loop", timeout.Token);

                var dispatcher = app.Services.GetRequiredService<SqliteWriteDispatcher>();
                // CAT-04: um sucesso aprovado, uma falha rejeitada, e uma tentativa em andamento.
                await InsertAttemptAsync(dispatcher, tenantId, project.Id, catBuild, 1, "approved", "completed", 0.5, 100, 40, 1_000, null, timeout.Token);
                await InsertAttemptAsync(dispatcher, tenantId, project.Id, catBuild, 2, "rejected", "failed", 0.25, 50, 10, 500, "functional gate failed", timeout.Token);
                await InsertAttemptAsync(dispatcher, tenantId, project.Id, catWire, 1, "running", "running", 0.25, 20, 0, null, null, timeout.Token);
                // PLAT-04: três falhas consecutivas com a MESMA razão => loop semântico.
                await InsertAttemptAsync(dispatcher, tenantId, project.Id, platStuck, 1, "rejected", "failed", 0.1, 10, 5, 200, "compilation error CS1002", timeout.Token);
                await InsertAttemptAsync(dispatcher, tenantId, project.Id, platStuck, 2, "rejected", "failed", 0.1, 10, 5, 200, "compilation error CS1002", timeout.Token);
                await InsertAttemptAsync(dispatcher, tenantId, project.Id, platStuck, 3, "rejected", "failed", 0.1, 10, 5, 200, "compilation error CS1002", timeout.Token);

                // 1) Métricas por feature.
                var metrics = await client.GetFromJsonAsync<FeatureMetricsResponse>(
                    $"/api/v1/governance-runtime/feature-metrics?projectId={project.Id}", timeout.Token);
                Assert.NotNull(metrics);
                Assert.Equal(project.Id, metrics.ProjectId);

                var cat = metrics.Features.Single(feature => feature.FeatureId == "CAT-04");
                Assert.Equal(2, cat.TaskCount);
                Assert.Equal(3, cat.AttemptCount);
                Assert.Equal(1, cat.SuccessCount);
                Assert.Equal(1, cat.FailureCount);
                Assert.Equal(1, cat.InProgressCount);
                Assert.Equal(1.0m, cat.TotalCostUsd);
                Assert.Equal(170, cat.TotalTokensInput);
                Assert.Equal(50, cat.TotalTokensOutput);
                Assert.Equal(1_500, cat.TotalDurationMs);

                var plat = metrics.Features.Single(feature => feature.FeatureId == "PLAT-04");
                Assert.Equal(1, plat.TaskCount);
                Assert.Equal(3, plat.AttemptCount);
                Assert.Equal(3, plat.FailureCount);
                Assert.Equal(0, plat.SuccessCount);

                // 2) Detecção de travamento: apenas a tarefa PLAT-04 está travada.
                var stuck = await client.GetFromJsonAsync<StuckTasksResponse>(
                    $"/api/v1/governance-runtime/stuck-tasks?projectId={project.Id}", timeout.Token);
                Assert.NotNull(stuck);
                var stuckTask = Assert.Single(stuck.Tasks);
                Assert.Equal(platStuck, stuckTask.TaskId);
                Assert.Equal("PLAT-04", stuckTask.FeatureId);
                Assert.Equal("RepeatedFailureReason", stuckTask.Reason);
                Assert.Equal(3, stuckTask.NoProgressStreak);

                // 3) Juiz determinístico (default, sem credenciais).
                using var passResponse = await client.PostAsJsonAsync(
                    "/api/v1/governance-runtime/eval-judge",
                    new EvalJudgeApiRequest(
                        ["Endpoint returns 200"], "diff --git a b", ["evidence://run/1"],
                        [new EvalJudgeTestApiContract("gate", true, "evidence://gate")]),
                    timeout.Token);
                passResponse.EnsureSuccessStatusCode();
                var passVerdict = (await passResponse.Content.ReadFromJsonAsync<EvalJudgeVerdictContract>(timeout.Token))!;
                Assert.True(passVerdict.Passed);
                Assert.Equal("deterministic-rule-based", passVerdict.Provider);
                Assert.Equal(1m, passVerdict.Score);

                using var failResponse = await client.PostAsJsonAsync(
                    "/api/v1/governance-runtime/eval-judge",
                    new EvalJudgeApiRequest(
                        ["Endpoint returns 200"], "diff --git a b", [],
                        [new EvalJudgeTestApiContract("gate", false, "evidence://gate")]),
                    timeout.Token);
                failResponse.EnsureSuccessStatusCode();
                var failVerdict = (await failResponse.Content.ReadFromJsonAsync<EvalJudgeVerdictContract>(timeout.Token))!;
                Assert.False(failVerdict.Passed);

                // Validação de entrada: projectId ausente/ inválido é recusado com 400.
                using var invalid = await client.GetAsync(
                    "/api/v1/governance-runtime/feature-metrics?projectId=not-a-ulid", timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<string> CreateTaskAsync(
        HttpClient client, string projectId, string title, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/tasks", new CreateTaskRequest(projectId, title, "Implement and validate."), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardTaskContract>(token))!.Id;
    }

    private static async Task InsertAttemptAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, string projectId, string taskId,
        int attemptNumber, string state, string operationalState, double costUsd, long tokensInput,
        long tokensOutput, long? durationMs, string? failureReason, CancellationToken token)
    {
        // A tentativa reutiliza a instrução v1 criada com a tarefa. Escrita direta no store durável:
        // controlamos desfecho e custo para provar a agregação determinística.
        var instructionVersionId = await dispatcher.ExecuteAsync(async (connection, ct) =>
        {
            await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT id FROM instruction_versions WHERE task_id=$task ORDER BY version LIMIT 1;";
            query.Parameters.AddWithValue("$task", taskId);
            return (string)(await query.ExecuteScalarAsync(ct))!;
        }, token);

        var startedAt = DateTimeOffset.UtcNow.AddSeconds(attemptNumber);
        await dispatcher.ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO work_attempts
                    (id,tenant_id,project_id,task_id,instruction_version_id,attempt_number,
                     producer_agent_id,state,started_at,completed_at,duration_ms,cost_usd,
                     tokens_input,tokens_output,operational_state,failure_reason)
                VALUES ($id,$tenant,$project,$task,$iv,$num,$agent,$state,$started,$completed,$dur,
                        $cost,$tin,$tout,$op,$fail);
                """;
            command.Parameters.AddWithValue("$id", UlidValue.New(startedAt).ToString());
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$task", taskId);
            command.Parameters.AddWithValue("$iv", instructionVersionId);
            command.Parameters.AddWithValue("$num", attemptNumber);
            command.Parameters.AddWithValue("$agent", "producer-agent");
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$started", startedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$completed",
                state == "running"
                    ? DBNull.Value
                    : startedAt.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$dur", (object?)durationMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$cost", costUsd);
            command.Parameters.AddWithValue("$tin", tokensInput);
            command.Parameters.AddWithValue("$tout", tokensOutput);
            command.Parameters.AddWithValue("$op", operationalState);
            command.Parameters.AddWithValue("$fail", (object?)failureReason ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }, token);
    }

    private static async Task<ProfileResponse> CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations", new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(HttpClient client, string organizationId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest
        {
            OrganizationId = organizationId,
            Name = "Poseidon",
            Key = "POSEIDON",
            Description = "Backend",
        }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
