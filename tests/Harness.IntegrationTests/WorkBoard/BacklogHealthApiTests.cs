using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.WorkBoard;

/// <summary>
/// RN-04 — SAÚDE DO BACKLOG. Prova que o read-model `GET /api/v1/backlog/health` está montado e
/// devolve a forma esperada: o limite aplicado, quantos cards varreu e a lista tipada de presos.
/// A DETECÇÃO em si (o que conta como preso) é provada, isolada, no BacklogHealthEvaluatorTests.
/// </summary>
public sealed class BacklogHealthApiTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harness-backlog-health-{Guid.NewGuid():N}");

    [Fact]
    public async Task TheBacklogHealthReadModelIsWiredAndReturnsAWellFormedResponse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Directory.CreateDirectory(_root);
        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(_root, "harness.db"),
        ]);
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };

        var projectId = await SeedProjectAsync(client, timeout.Token);
        // Um card ativo recém-criado: existe, é varrido, mas não está preso (acabou de se mover).
        using var task = await client.PostAsJsonAsync(
            "/api/v1/tasks",
            new CreateTaskRequest(projectId, "Card ativo", "Instrução do card."),
            timeout.Token);
        task.EnsureSuccessStatusCode();

        using var response = await client.GetAsync(
            $"/api/v1/backlog/health?projectId={projectId}&thresholdMinutes=120", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = body.RootElement;
        Assert.Equal(120, root.GetProperty("thresholdMinutes").GetInt64());
        Assert.True(root.GetProperty("scannedCards").GetInt32() >= 1);
        Assert.Equal(0, root.GetProperty("stuckCards").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("stuck").ValueKind);
        Assert.Empty(root.GetProperty("stuck").EnumerateArray());

        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task AnInvalidThresholdIsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Directory.CreateDirectory(_root);
        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(_root, "harness.db"),
        ]);
        await app.StartAsync(timeout.Token);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress(app.Services) };
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Operador", null, null, "pt-BR"), timeout.Token);
        profile.EnsureSuccessStatusCode();

        using var response = await client.GetAsync(
            "/api/v1/backlog/health?thresholdMinutes=0", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "invalid_threshold",
            await response.Content.ReadAsStringAsync(timeout.Token),
            StringComparison.Ordinal);
        await app.StopAsync(timeout.Token);
    }

    private static async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Operador", null, null, "pt-BR"), token);
        profile.EnsureSuccessStatusCode();
        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, token);
        organization.EnsureSuccessStatusCode();
        using var organizationBody = JsonDocument.Parse(await organization.Content.ReadAsStringAsync(token));
        var organizationId = organizationBody.RootElement.GetProperty("id").GetString()!;
        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Projeto do teste de saúde do backlog.",
            },
            token);
        project.EnsureSuccessStatusCode();
        using var projectBody = JsonDocument.Parse(await project.Content.ReadAsStringAsync(token));
        return projectBody.RootElement.GetProperty("id").GetString()!;
    }

    private static Uri BaseAddress(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
