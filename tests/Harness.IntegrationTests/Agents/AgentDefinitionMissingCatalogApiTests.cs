using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Modules.Identity.Contracts;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// CAT-05: quando uma definição referencia um item de catálogo (skill/tool/model) que não existe, a
/// API não falha em silêncio — devolve um problema tipado e ACIONÁVEL, com o catálogo faltante, a
/// referência exata e a rota para criá-lo ("criar quando não encontrar").
/// </summary>
public sealed class AgentDefinitionMissingCatalogApiTests
{
    [Fact]
    public async Task ReferencingAMissingSkillReturnsAnActionableTypedProblem()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"cat05-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "cat05.db");
        Directory.CreateDirectory(root);
        var cookies = new CookieContainer();

        try
        {
            await using var app = CreateHost(database);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                using (var profile = await client.PostAsJsonAsync(
                    "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                {
                    profile.EnsureSuccessStatusCode();
                }

                // Um id de skill sintaticamente válido (ULID) mas que não existe no catálogo global.
                var missingSkillId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
                var definition = new AgentDefinitionWriteRequest(
                    "orphan-reviewer", "Orphan Reviewer", "specialist", null,
                    "References a skill that does not exist.", null, [missingSkillId], [],
                    null, null, [], [], [], null, []);

                using var response = await client.PostAsJsonAsync(
                    "/api/v1/agent-definitions", definition, timeout.Token);

                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var problem = body.RootElement;
                Assert.Equal("agent_definition_missing_catalog_item", problem.GetProperty("title").GetString());
                Assert.Equal("skill", problem.GetProperty("catalog").GetString());
                Assert.Equal(missingSkillId, problem.GetProperty("reference").GetString());
                Assert.Equal("/api/v1/skills", problem.GetProperty("createRoute").GetString());
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

    [Fact]
    public async Task ReferencingAMissingToolReturnsTheToolCreateRoute()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"cat05t-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "cat05t.db");
        Directory.CreateDirectory(root);
        var cookies = new CookieContainer();

        try
        {
            await using var app = CreateHost(database);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                using (var profile = await client.PostAsJsonAsync(
                    "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                {
                    profile.EnsureSuccessStatusCode();
                }

                var missingToolId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
                var definition = new AgentDefinitionWriteRequest(
                    "orphan-tool-user", "Orphan Tool User", "specialist", null,
                    "References a tool that does not exist.", null, [], [missingToolId],
                    null, null, [], [], [], null, []);

                using var response = await client.PostAsJsonAsync(
                    "/api/v1/agent-definitions", definition, timeout.Token);

                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                Assert.Equal("tool", body.RootElement.GetProperty("catalog").GetString());
                Assert.Equal("/api/v1/tools", body.RootElement.GetProperty("createRoute").GetString());
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

    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
