using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harness.Host;
using Harness.Host.Agents;
using Harness.Host.Profiles;
using Harness.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// CAT-06 — prova end-to-end do import/export portável de definições de agente: round-trip
/// idempotente em JSON e YAML, rejeição tipada de documento inválido, colisão de chave
/// resolvida de forma determinística e reaproveitamento da validação existente (referência
/// de especialidade inexistente é recusada).
/// </summary>
public sealed class AgentDefinitionPortabilityApiTests
{
    [Fact]
    public async Task JsonRoundTripIsIdempotentAndKeyCollisionUpdatesInPlace()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var database = NewDatabase();
        await using var app = CreateHost(database);
        await app.StartAsync(timeout.Token);
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
        await CreateProfileAsync(client, timeout.Token);
        var created = await CreateDefinitionAsync(client, "importer-spec", "Importer Spec", timeout.Token);

        // export -> import -> export must be byte-identical (idempotent).
        var exported = await ExportAsync(client, $"/api/v1/agent-definitions/{created.Id}/export?format=json", timeout.Token);
        Assert.Contains("\"key\": \"importer-spec\"", exported, StringComparison.Ordinal);
        var import = await ImportAsync(client, exported, "json", null, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var result = (await import.Content.ReadFromJsonAsync<AgentImportResultContract>(timeout.Token))!;
        var item = Assert.Single(result.Imported);
        Assert.Equal("updated", item.Outcome);
        Assert.Equal(created.Id, item.Id);
        var reExported = await ExportAsync(client, $"/api/v1/agent-definitions/{created.Id}/export?format=json", timeout.Token);
        Assert.Equal(exported, reExported);

        // Importing the same key again keeps updating in place — the catalog does not grow.
        var before = await CountDefinitionsAsync(client, timeout.Token);
        var reimport = await ImportAsync(client, exported, "json", null, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, reimport.StatusCode);
        Assert.Equal(before, await CountDefinitionsAsync(client, timeout.Token));

        // onConflict=fork forges a stable non-colliding key instead of updating.
        var fork = await ImportAsync(client, exported, "json", "fork", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, fork.StatusCode);
        var forked = Assert.Single((await fork.Content.ReadFromJsonAsync<AgentImportResultContract>(timeout.Token))!.Imported);
        Assert.Equal("created", forked.Outcome);
        Assert.Equal("importer-spec-2", forked.Key);
        Assert.NotEqual(created.Id, forked.Id);
        Assert.Equal(before + 1, await CountDefinitionsAsync(client, timeout.Token));

        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task YamlRoundTripIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var database = NewDatabase();
        await using var app = CreateHost(database);
        await app.StartAsync(timeout.Token);
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
        await CreateProfileAsync(client, timeout.Token);
        var created = await CreateDefinitionAsync(client, "yaml-spec", "Yaml Spec", timeout.Token);

        var exported = await ExportAsync(client, $"/api/v1/agent-definitions/{created.Id}/export?format=yaml", timeout.Token);
        Assert.Contains("key: yaml-spec", exported, StringComparison.Ordinal);

        var import = await ImportAsync(client, exported, "yaml", null, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Equal("updated", Assert.Single((await import.Content.ReadFromJsonAsync<AgentImportResultContract>(timeout.Token))!.Imported).Outcome);

        var reExported = await ExportAsync(client, $"/api/v1/agent-definitions/{created.Id}/export?format=yaml", timeout.Token);
        Assert.Equal(exported, reExported);

        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task InvalidDocumentsAndReferencesAreRejectedWithTypedBadRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var database = NewDatabase();
        await using var app = CreateHost(database);
        await app.StartAsync(timeout.Token);
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
        await CreateProfileAsync(client, timeout.Token);

        // Unknown field is refused — the document cannot smuggle extra members.
        var unknown = await ImportAsync(client,
            "{\"schemaVersion\":\"harness.agent-definition/v1\",\"definitions\":[{\"key\":\"x\",\"name\":\"X\",\"role\":\"specialist\",\"description\":\"d\",\"owner\":\"root\"}]}",
            "json", null, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Invalid role is refused by the reused content validation.
        var invalidRole = await ImportAsync(client,
            "{\"definitions\":[{\"key\":\"y\",\"name\":\"Y\",\"role\":\"emperor\",\"description\":\"d\"}]}",
            "json", null, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidRole.StatusCode);

        // A specialty that is not registered in the catalog is refused (CAT-04 reference check).
        var missingSpecialty = await ImportAsync(client,
            "{\"definitions\":[{\"key\":\"z\",\"name\":\"Z\",\"role\":\"specialist\",\"description\":\"d\",\"specialty\":\"nonexistent-specialty\"}]}",
            "json", null, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, missingSpecialty.StatusCode);

        // Bogus format query is refused.
        using var badFormat = await client.PostAsync("/api/v1/agent-definitions/import?format=xml",
            new StringContent("{}", Encoding.UTF8, "application/json"), timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, badFormat.StatusCode);

        await app.StopAsync(timeout.Token);
    }

    private static async Task<AgentDefinitionContract> CreateDefinitionAsync(HttpClient client, string key, string name, CancellationToken token)
    {
        var request = new AgentDefinitionWriteRequest(
            key, name, "specialist", null, "Portable definition",
            null, [], [], "Persona text", "Mission statement",
            ["principle-a", "principle-b"], ["deliverable-a"], ["quality-a"], "concise", ["no secrets"],
            ["dotnet", "aspnet"], "high", null, [], null, "actor", "low");
        using var response = await client.PostAsJsonAsync("/api/v1/agent-definitions", request, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentDefinitionContract>(token))!;
    }

    private static async Task<string> ExportAsync(HttpClient client, string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string payload, string format, string? onConflict, CancellationToken token)
    {
        var contentType = format == "yaml" ? "application/yaml" : "application/json";
        var url = $"/api/v1/agent-definitions/import?format={format}";
        if (onConflict is not null) url += $"&onConflict={onConflict}";
        return client.PostAsync(url, new StringContent(payload, Encoding.UTF8, contentType), token);
    }

    private static async Task<int> CountDefinitionsAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync("/api/v1/agent-definitions?includeArchived=true&limit=200", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentDefinitionPage>(token))!.Items.Count;
    }

    private static async Task CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
    }

    private static string NewDatabase()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"portability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "portability.db");
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
