using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Governance;

/// <summary>
/// Fase 5 — o Evaluation Service responde pelo caminho REAL: tentativas duráveis do WorkChain
/// (aprovadas e rejeitadas por revisor distinto) agregadas por agente produtor e cruzadas com o
/// espelho `model_invocations` (Fase 3) viram score composto, intervalo de confiança de Wilson e
/// recomendação tipada — por agente, modelo e provedor, com evidência numérica. Amostra abaixo do
/// mínimo NUNCA vira recomendação de desempenho: vira `insufficient_sample_size`.
/// </summary>
public sealed class EvaluationRecommendationApiTests
{
    [Fact]
    public async Task RecommendationsDeriveFromRecordedAttemptsAndInvocations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"eval-recommendations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "eval.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                var projectId = await SeedProjectAsync(client, timeout.Token);
                var tenantId = await TenantIdAsync(app);
                var invocations = app.Services.GetRequiredService<IModelInvocationStore>();

                // prod-alpha: duas assinaturas distintas, cada uma com 3 tentativas aprovadas.
                // Agente/provedor/modelo iguais NÃO podem colapsar identidades de conta.
                for (var index = 0; index < 6; index++)
                {
                    var attemptId = await SeedReviewedAttemptAsync(
                        app, client, projectId, $"Card alpha {index}", "prod-alpha", "approved",
                        timeout.Token);
                    var accountAlias = index < 3
                        ? "subscription-primary"
                        : "subscription-secondary";
                    await invocations.RecordInvocationAsync(
                        new ModelInvocationRecord(
                            UlidValue.New(DateTimeOffset.UtcNow).ToString(),
                            tenantId,
                            projectId,
                            attemptId.TaskId,
                            attemptId.AttemptId,
                            "codex",
                            "gpt-5-codex",
                            accountAlias,
                            1200,
                            300,
                            0.04m,
                            60_000,
                            "completed",
                            DateTimeOffset.UtcNow),
                        timeout.Token);
                }

                // prod-beta: 1 tentativa rejeitada, sem invocação espelhada.
                _ = await SeedReviewedAttemptAsync(
                    app, client, projectId, "Card beta", "prod-beta", "rejected", timeout.Token);

                using var response = await client.GetAsync(
                    $"/api/v1/governance-runtime/evaluation-recommendations?projectId={projectId}&minSampleSize=3",
                    timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));

                var aggregates = body.RootElement.GetProperty("aggregates").EnumerateArray().ToArray();
                Assert.Equal(3, aggregates.Length);

                var alpha = aggregates
                    .Where(item => item.GetProperty("targetId").GetString() == "prod-alpha")
                    .OrderBy(item => item.GetProperty("accountAlias").GetString(), StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(2, alpha.Length);
                Assert.Equal(
                    "subscription-primary",
                    alpha[0].GetProperty("accountAlias").GetString());
                Assert.Equal(
                    "subscription-secondary",
                    alpha[1].GetProperty("accountAlias").GetString());
                Assert.All(alpha, aggregate =>
                {
                    Assert.Equal("codex", aggregate.GetProperty("provider").GetString());
                    Assert.Equal("gpt-5-codex", aggregate.GetProperty("model").GetString());
                    Assert.Equal(3, aggregate.GetProperty("sampleSize").GetInt32());
                    Assert.Equal(3, aggregate.GetProperty("successCount").GetInt32());
                    Assert.Equal(1.0, aggregate.GetProperty("passRate").GetDouble());
                    Assert.Equal(1.0, aggregate.GetProperty("compositeScore").GetDouble());
                    Assert.True(aggregate.GetProperty("sampleSizeQualified").GetBoolean());
                    var lower = aggregate.GetProperty("confidenceIntervalLower").GetDouble();
                    Assert.InRange(lower, 0.30, 0.99);
                    Assert.True(
                        lower < 1.0,
                        "IC de Wilson nunca degenera em certeza com amostra 3.");
                });

                var beta = Assert.Single(
                    aggregates, item => item.GetProperty("targetId").GetString() == "prod-beta");
                Assert.True(beta.GetProperty("provider").ValueKind == JsonValueKind.Null);
                Assert.Equal(1, beta.GetProperty("sampleSize").GetInt32());
                Assert.Equal(0, beta.GetProperty("successCount").GetInt32());
                Assert.False(beta.GetProperty("sampleSizeQualified").GetBoolean());

                var recommendations = body.RootElement.GetProperty("recommendations")
                    .EnumerateArray().ToArray();
                var alphaRecommendations = recommendations
                    .Where(item => item.GetProperty("targetId").GetString() == "prod-alpha")
                    .ToArray();
                Assert.Equal(2, alphaRecommendations.Length);
                Assert.All(alphaRecommendations, recommendation =>
                {
                    Assert.Equal(
                        "maintain_current_routing",
                        recommendation.GetProperty("action").GetString());
                    var accountAlias = recommendation.GetProperty("accountAlias").GetString();
                    Assert.True(
                        accountAlias is "subscription-primary" or "subscription-secondary");
                });
                Assert.Equal(
                    "insufficient_sample_size",
                    Assert.Single(
                        recommendations,
                        item => item.GetProperty("targetId").GetString() == "prod-beta")
                        .GetProperty("action").GetString());
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

    /// <summary>
    /// Semeia a cadeia durável real (solicitação → demanda → task → tentativa) e leva a
    /// tentativa até o veredito do revisor distinto — o mesmo caminho do produto, sem atalhos.
    /// </summary>
    private static async Task<(string TaskId, string AttemptId)> SeedReviewedAttemptAsync(
        WebApplication app,
        HttpClient client,
        string projectId,
        string title,
        string producerAgentId,
        string decision,
        CancellationToken token)
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
        var demandId = demandBody.RootElement.GetProperty("id").GetString()!;

        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, title, $"Implementar {title}.", demandId),
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
                tenantId,
                persisted.BackingSolicitationId,
                taskId,
                instruction.Id,
                attemptId,
                producerAgentId,
                persisted.Version,
                $"eval-recommendations:start:{attemptId}",
                now),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, started.Status);

        var completed = await chain.CompleteAttemptAsync(
            new WorkAttemptCompleteCommand(
                tenantId,
                persisted.BackingSolicitationId,
                taskId,
                attemptId,
                started.TaskVersion!.Value,
                [new WorkEvidenceInput(
                    UlidValue.New(now.AddMilliseconds(1)).ToString(),
                    $"tests:eval-recommendations:{attemptId}")],
                $"eval-recommendations:complete:{attemptId}",
                now.AddMilliseconds(1)),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, completed.Status);

        var reviewed = await chain.ReviewAttemptAsync(
            new WorkAttemptReviewCommand(
                tenantId,
                persisted.BackingSolicitationId,
                taskId,
                attemptId,
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                "independent-reviewer",
                decision,
                $"Veredito {decision} do gate objetivo.",
                completed.TaskVersion!.Value,
                $"eval-recommendations:review:{attemptId}",
                now.AddMilliseconds(2)),
            token);
        Assert.Equal(WorkChainMutationStatus.Applied, reviewed.Status);
        return (taskId, attemptId);
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
                Description = "Projeto da avaliação estatística.",
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
