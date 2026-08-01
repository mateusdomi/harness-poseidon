using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Providers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// Fase 1D — produtividade por assinatura no painel de confiabilidade.
///
/// O ponto da prova é o VIÉS que a medida tinha: pass@k percorria somente os cards com
/// classificação MAST, ou seja, apenas aqueles em que a equipe já havia falhado. Um projeto que
/// executou bem aparecia com capacidade VAZIA — e chamar isso de "produtividade" mente para quem
/// decide com base nela. Aqui não há nenhuma classificação de falha, e mesmo assim a medida existe.
/// </summary>
public sealed class ProjectReliabilityApiTests
{
    [Fact]
    public async Task ProductivityIsMeasuredOverTheWholeProjectNotOnlyOverFailedCards()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"reliability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "reliability.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                (await client.PostAsJsonAsync("/api/v1/profiles",
                    new CreateProfileRequest("Operador", null, null, "pt-BR"), timeout.Token))
                    .EnsureSuccessStatusCode();
                var orgId = await PostId(client, "/api/v1/organizations",
                    new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token);
                var projectId = await PostId(client, "/api/v1/projects",
                    new CreateProjectRequest
                    {
                        OrganizationId = orgId,
                        Name = "Poseidon",
                        Key = "PSD",
                        Description = "Control plane",
                    }, timeout.Token);

                var tenantId = (await app.Services.GetRequiredService<ILocalProfileStore>()
                    .ListAsync(timeout.Token))[0].TenantId;
                var invocations = app.Services.GetRequiredService<IModelInvocationStore>();

                // Duas assinaturas, dois cards, nenhuma classificação MAST. A principal acerta de
                // primeira; a secundária erra e acerta na segunda rodada do mesmo card.
                var cardA = UlidValue.New(DateTimeOffset.UtcNow).ToString();
                var cardB = UlidValue.New(DateTimeOffset.UtcNow.AddMilliseconds(1)).ToString();
                await RecordAsync(invocations, tenantId, projectId, cardA, "assinatura-principal",
                    "success", DateTimeOffset.UtcNow, 1000, 2.5m, timeout.Token);
                await RecordAsync(invocations, tenantId, projectId, cardB, "assinatura-secundaria",
                    "failed", DateTimeOffset.UtcNow.AddSeconds(1), 500, 1m, timeout.Token);
                await RecordAsync(invocations, tenantId, projectId, cardB, "assinatura-secundaria",
                    "success", DateTimeOffset.UtcNow.AddSeconds(2), 700, 1.5m, timeout.Token);

                using var response = await client.GetAsync(
                    $"/api/v1/projects/{projectId}/reliability?k=3", timeout.Token);
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));

                Assert.Equal(0, body.RootElement.GetProperty("classifiedAttempts").GetInt32());
                Assert.False(body.RootElement.GetProperty("sampleTruncated").GetBoolean());

                var subscriptions = body.RootElement.GetProperty("subscriptions").EnumerateArray().ToArray();
                Assert.Equal(2, subscriptions.Length);

                var secondary = subscriptions.Single(item =>
                    item.GetProperty("accountAlias").GetString() == "assinatura-secundaria");
                Assert.Equal(2, secondary.GetProperty("invocations").GetInt32());
                Assert.Equal(1, secondary.GetProperty("tasksTouched").GetInt32());
                Assert.Equal(1, secondary.GetProperty("successes").GetInt32());
                Assert.Equal(1600, secondary.GetProperty("totalTokens").GetInt64()); // (200+500) + (200+700)
                Assert.Equal(2.5m, secondary.GetProperty("estimatedCostUsd").GetDecimal());

                var primary = subscriptions.Single(item =>
                    item.GetProperty("accountAlias").GetString() == "assinatura-principal");
                Assert.Equal(1, primary.GetProperty("invocations").GetInt32());

                // A medida de capacidade EXISTE mesmo sem nenhuma falha classificada — era
                // exatamente isso que o recorte anterior impedia.
                var capabilities = body.RootElement.GetProperty("capabilities").EnumerateArray().ToArray();
                Assert.NotEmpty(capabilities);
                var measured = capabilities.Single(item =>
                    item.GetProperty("accountAlias").GetString() == "assinatura-secundaria");
                Assert.Equal(0d, measured.GetProperty("passAt1").GetDouble());
                Assert.Equal(1d, measured.GetProperty("passAtK").GetDouble());
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // limpeza best-effort
            }
        }
    }

    private static Task RecordAsync(
        IModelInvocationStore invocations,
        string tenantId,
        string projectId,
        string taskId,
        string alias,
        string outcome,
        DateTimeOffset at,
        int outputTokens,
        decimal cost,
        CancellationToken token) =>
        invocations.RecordInvocationAsync(
            new ModelInvocationRecord(
                UlidValue.New(at).ToString(), tenantId, projectId, taskId,
                UlidValue.New(at.AddTicks(1)).ToString(),
                "anthropic", "opus", alias, 200, outputTokens, cost, 1000, outcome, at),
            token);

    private static async Task<string> PostId<T>(HttpClient client, string route, T body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(route, body, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("id").GetString()!;
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
