using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Governance;

public sealed class LearningCandidateApiTests
{
    [Fact]
    public async Task RealHostPublishesDemoScopedLearningPipelineWithoutMocks()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"learning-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var host = HostApplication.Build(["--urls","http://127.0.0.1:0","--Harness:DatabasePath",Path.Combine(root,"harness.db"),
            "--Harness:Demo:Enabled","true"]);
        try
        {
            await host.StartAsync(timeout.Token);
            var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var client = new HttpClient(handler) { BaseAddress = address };
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/governance-runtime/learning-candidates");
            request.Headers.Add("Idempotency-Key", "api-create");
            request.Content = JsonContent.Create(new
            {
                projectId = "01J00000000000000000000004",
                type = "rule",
                observation = "Transient failures should retry",
                evidence = new[] { new { kind = "test", reference = "test://retry", checksum = "sha256:" + new string('a', 64), summary = "fixture" } },
                payload = new { title = "Retry transient failures", statement = "Retry only transient failures." },
                actorAgentId = "01J00000000000000000000005",
                actorProvider = "fake",
                actorModel = "actor-model",
                baselineVersion = "rule/1",
                proposedVersion = "rule/2"
            });
            using var response = await client.SendAsync(request, timeout.Token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var candidate = body.RootElement.GetProperty("candidate");
            Assert.Equal("candidate", candidate.GetProperty("state").GetString());
            var id = candidate.GetProperty("candidateId").GetString();
            using var list = await client.GetAsync("/api/v1/governance-runtime/learning-candidates?projectId=01J00000000000000000000004", timeout.Token);
            list.EnsureSuccessStatusCode();
            Assert.Contains(id!, await list.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
            var review = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/governance-runtime/learning-candidates/{id}/review");
            review.Headers.Add("Idempotency-Key", "api-review"); review.Content = JsonContent.Create(new { expectedVersion = 1, note = "review requested" });
            using var reviewed = await client.SendAsync(review, timeout.Token); reviewed.EnsureSuccessStatusCode();
            Assert.Contains("in_review", await reviewed.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);

            await AssertStateAsync(client, id!, "evaluation-request", "api-evaluation-request",
                new { expectedVersion = 2, note = "independent evaluation requested" }, "awaiting_evaluation", timeout.Token);
            using var handoff = await client.PostAsJsonAsync(
                "/api/v1/projects/01J00000000000000000000004/chief/handoff",
                new { targetDefinitionId = (string?)null, targetModelId = (string?)null, note = "Create independent evaluator." }, timeout.Token);
            handoff.EnsureSuccessStatusCode();
            using var handoffBody = JsonDocument.Parse(await handoff.Content.ReadAsStringAsync(timeout.Token));
            var evaluatorId = handoffBody.RootElement.GetProperty("id").GetString();
            Assert.NotEqual("01J00000000000000000000005", evaluatorId);
            await AssertStateAsync(client, id!, "evaluations", "api-evaluation", new
            {
                expectedVersion = 3,
                evaluatorAgentId = evaluatorId,
                evaluatorProvider = "independent",
                evaluatorModel = "critic-model",
                verdict = "pass",
                note = "independent evaluation passed"
            }, "evaluated", timeout.Token);
            await AssertStateAsync(client, id!, "shadow", "api-shadow", new
            {
                expectedVersion = 4,
                result = new
                {
                    sampleSize = 30,
                    firstPassSuccessDelta = 0.12m,
                    repeatedErrorRateDelta = -0.08m,
                    tokenImpact = -120,
                    costPerAcceptedTaskDelta = -0.05m,
                    regressions = 0,
                    evidenceReference = "evidence://shadow/api"
                },
                note = "shadow passed"
            }, "shadow", timeout.Token);
            await AssertStateAsync(client, id!, "decision", "api-approval",
                new { expectedVersion = 5, approved = true, note = "human approval" }, "approved", timeout.Token);
            await AssertStateAsync(client, id!, "promotion", "api-promotion",
                new { expectedVersion = 6, note = "manual promotion" }, "promoted", timeout.Token);

            using var metrics = await client.GetAsync(
                "/api/v1/governance-runtime/learning-candidates/metrics?projectId=01J00000000000000000000004", timeout.Token);
            metrics.EnsureSuccessStatusCode();
            using (var metricsBody = JsonDocument.Parse(await metrics.Content.ReadAsStringAsync(timeout.Token)))
            {
                Assert.Equal(1, metricsBody.RootElement.GetProperty("promoted").GetInt32());
                Assert.Equal(-120, metricsBody.RootElement.GetProperty("tokenImpact").GetInt64());
            }
            await AssertStateAsync(client, id!, "rollback", "api-rollback",
                new { expectedVersion = 7, note = "regression detected" }, "rolled_back", timeout.Token);
            await AssertStateAsync(client, id!, "deprecation", "api-deprecation",
                new { expectedVersion = 8, note = "superseded safely" }, "deprecated", timeout.Token);
            using var history = await client.GetAsync($"/api/v1/governance-runtime/learning-candidates/{id}/history", timeout.Token);
            history.EnsureSuccessStatusCode();
            using var historyBody = JsonDocument.Parse(await history.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal(9, historyBody.RootElement.GetArrayLength());
        }
        finally
        {
            await host.StopAsync(timeout.Token); await host.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task AssertStateAsync<T>(HttpClient client, string candidateId, string action,
        string idempotencyKey, T payload, string expectedState, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/governance-runtime/learning-candidates/{candidateId}/{action}");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.Equal(expectedState, body.RootElement.GetProperty("state").GetString());
    }
}
