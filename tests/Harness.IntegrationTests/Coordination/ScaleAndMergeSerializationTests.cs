using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Coordination;

/// <summary>
/// Fase 10 — paralelismo em escala no caminho REAL:
/// (a) o plano de demanda expõe o grafo provides/consumes como ondas de despacho e barreiras de
/// fan-in derivadas das dependências declaradas dos cards — pré-requisitos antes da
/// implementação, integração por último, atrás de barreira;
/// (b) TODO merge do WorkChain passa pela fila única serializada e a contenção é MEDIDA e
/// consultável — o gatilho decidido em arquitetura para reavaliar infra de fila.
/// </summary>
public sealed class ScaleAndMergeSerializationTests
{
    [Fact]
    public async Task PlanExposesDependencyWavesAndMergeGoesThroughTheSerializedQueue()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"scale-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "scale.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                var projectId = await SeedProjectAsync(client, timeout.Token);

                // (a) Plano com incerteza + frontend: o grafo precisa ordenar spike antes das
                // implementações e a integração atrás da barreira de fan-in.
                var demandId = await SeedDemandAsync(
                    client, projectId, "SCL-01: Exportação com incerteza técnica", timeout.Token);
                using var planResponse = await client.PostAsJsonAsync(
                    $"/api/v1/demands/{demandId}/plan",
                    new
                    {
                        hints = new
                        {
                            hasFrontendSurface = true,
                            hasTechnicalUncertainty = true,
                        },
                    },
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Created, planResponse.StatusCode);
                using var plan = JsonDocument.Parse(
                    await planResponse.Content.ReadAsStringAsync(timeout.Token));

                Assert.Empty(plan.RootElement.GetProperty("dependencyIssues").EnumerateArray());
                var waves = plan.RootElement.GetProperty("dispatchWaves").EnumerateArray()
                    .Select(wave => wave.EnumerateArray()
                        .Select(item => item.GetString()!).ToArray())
                    .ToArray();
                Assert.True(waves.Length >= 3, "spike -> implementações -> integração.");

                var cards = plan.RootElement.GetProperty("cards").EnumerateArray().ToArray();
                var spike = cards
                    .Single(card => card.GetProperty("cardType").GetString() == "spike")
                    .GetProperty("proposedTitle").GetString()!.Split(' ')[0];
                // A integração é o ÚLTIMO card do plano e é gate humano (o merge é humano por
                // regra); antes ela era um agent_task de papel crítico, que nunca poderia ser
                // despachado porque o papel crítico não possui escopo de escrita.
                var integration = cards[^1]
                    .GetProperty("proposedTitle").GetString()!.Split(' ')[0];
                Assert.Equal("human_gate", cards[^1].GetProperty("cardType").GetString());

                // O spike destrava as implementações: primeira onda; integração: última onda.
                Assert.Contains(spike, waves[0]);
                Assert.Contains(integration, waves[^1]);

                // A integração espera TODAS as implementações — barreira de fan-in explícita.
                var barrier = Assert.Single(
                    plan.RootElement.GetProperty("fanInBarriers").EnumerateArray().ToArray(),
                    item => item.GetProperty("consumerCard").GetString() == integration);
                Assert.True(barrier.GetProperty("providerCards").GetArrayLength() >= 2);

                // (b) Merge real de uma task aprovada: o decorator serializa e MEDE.
                var chain = app.Services.GetRequiredService<IWorkChainStore>();
                Assert.IsType<SerializedMergeWorkChainStore>(chain);
                var (taskId, solicitationId, taskVersion) = await SeedApprovedTaskAsync(
                    app, client, projectId, timeout.Token);
                var tenantId = await TenantIdAsync(app);
                var merged = await chain.MergeApprovedTaskAsync(
                    new WorkTaskMergeCommand(
                        tenantId,
                        solicitationId,
                        taskId,
                        "merge-coordinator",
                        "checkpoint:scale-merge",
                        taskVersion,
                        $"scale-merge:merge:{taskId}",
                        DateTimeOffset.UtcNow),
                    timeout.Token);
                Assert.Equal(WorkChainMutationStatus.Applied, merged.Status);

                using var contention = await client.GetAsync(
                    "/api/v1/governance-runtime/merge-contention", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, contention.StatusCode);
                using var snapshot = JsonDocument.Parse(
                    await contention.Content.ReadAsStringAsync(timeout.Token));
                Assert.True(snapshot.RootElement.GetProperty("enqueued").GetInt64() >= 1);
                Assert.True(snapshot.RootElement.GetProperty("serialized").GetInt64() >= 1);
                Assert.Equal(0, snapshot.RootElement.GetProperty("active").GetInt32());
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
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<(string TaskId, string SolicitationId, long TaskVersion)> SeedApprovedTaskAsync(
        WebApplication app, HttpClient client, string projectId, CancellationToken token)
    {
        var demandId = await SeedDemandAsync(client, projectId, "SCL-02: Card para merge", token);
        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, "SCL-02: Card para merge", "Implementar.", demandId),
            token);
        task.EnsureSuccessStatusCode();
        using var taskBody = JsonDocument.Parse(await task.Content.ReadAsStringAsync(token));
        var taskId = taskBody.RootElement.GetProperty("id").GetString()!;

        var tenantId = await TenantIdAsync(app);
        var board = app.Services.GetRequiredService<IWorkBoardStore>();
        var chain = app.Services.GetRequiredService<IWorkChainStore>();
        var persisted = (await board.GetTaskAsync(tenantId, taskId, token))!;
        var instruction = (await board.ListInstructionsAsync(tenantId, taskId, null, 10, token))[0];
        var now = DateTimeOffset.UtcNow;
        var attemptId = UlidValue.New(now).ToString();

        var started = await chain.StartAttemptAsync(
            new WorkAttemptStartCommand(
                tenantId, persisted.BackingSolicitationId, taskId, instruction.Id, attemptId,
                "prod-merge", persisted.Version, $"scale-merge:start:{attemptId}", now),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, started.Status);
        var completed = await chain.CompleteAttemptAsync(
            new WorkAttemptCompleteCommand(
                tenantId, persisted.BackingSolicitationId, taskId, attemptId,
                started.TaskVersion!.Value,
                [new WorkEvidenceInput(
                    UlidValue.New(now.AddMilliseconds(1)).ToString(), "tests:scale-merge")],
                $"scale-merge:complete:{attemptId}", now.AddMilliseconds(1)),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);
        var reviewed = await chain.ReviewAttemptAsync(
            new WorkAttemptReviewCommand(
                tenantId, persisted.BackingSolicitationId, taskId, attemptId,
                UlidValue.New(now.AddMilliseconds(2)).ToString(), "independent-reviewer",
                "approved", "Gate objetivo aprovado.", completed.TaskVersion!.Value,
                $"scale-merge:review:{attemptId}", now.AddMilliseconds(2)),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, reviewed.Status);
        return (taskId, persisted.BackingSolicitationId, reviewed.TaskVersion!.Value);
    }

    private static async Task<string> SeedDemandAsync(
        HttpClient client, string projectId, string title, CancellationToken token)
    {
        using var solicitation = await client.PostAsJsonAsync(
            "/api/v1/solicitations",
            new CreateSolicitationRequest(projectId, "request", title, $"Escopo de {title}."),
            token);
        solicitation.EnsureSuccessStatusCode();
        using var solicitationBody = JsonDocument.Parse(
            await solicitation.Content.ReadAsStringAsync(token));
        var solicitationId = solicitationBody.RootElement.GetProperty("id").GetString()!;

        using var demand = await client.PostAsJsonAsync(
            "/api/v1/demands",
            new CreateDemandRequest(projectId, title, $"Entregar {title}.", solicitationId),
            token);
        demand.EnsureSuccessStatusCode();
        using var demandBody = JsonDocument.Parse(await demand.Content.ReadAsStringAsync(token));
        return demandBody.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> TenantIdAsync(WebApplication app)
    {
        var profiles = await app.Services
            .GetRequiredService<ILocalProfileStore>()
            .ListAsync(CancellationToken.None);
        return profiles[0].TenantId;
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
                Description = "Projeto do paralelismo em escala.",
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
