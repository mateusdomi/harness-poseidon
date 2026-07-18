using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Organizations;

public sealed class OrganizationApiTests
{
    [Fact]
    public async Task OrganizationIsSessionScopedUniqueAndDurableAcrossRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"organization-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "organization.db");
        Directory.CreateDirectory(artifactRoot);
        var cookies = new CookieContainer();
        string organizationId;

        try
        {
            await using (var app = CreateHost(databasePath))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = GetBaseAddress(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var unauthorized = await anonymous.GetAsync(
                        "/api/v1/organizations", timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                        Assert.Equal("application/problem+json", unauthorized.Content.Headers.ContentType?.MediaType);
                    }

                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using var profileResponse = await client.PostAsJsonAsync(
                        "/api/v1/profiles",
                        new CreateProfileRequest("Mateus", null, null, "pt-BR"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, profileResponse.StatusCode);

                    using var createdResponse = await client.PostAsJsonAsync(
                        "/api/v1/organizations",
                        new CreateOrganizationRequest
                        {
                            Name = "Poseidon",
                            Slug = "poseidon",
                            Plan = "team",
                            Brand = new OrganizationBrandContract(
                                "logo.svg", "#123456", "#abcdef", "Inter"),
                        },
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
                    var created = await createdResponse.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token);
                    Assert.NotNull(created);
                    organizationId = created.Id;
                    Assert.Equal("poseidon", created.Slug);
                    Assert.Equal("#123456", created.Brand.PrimaryColor);
                    Assert.Empty(created.Policies);

                    using var duplicate = await client.PostAsJsonAsync(
                        "/api/v1/organizations",
                        new CreateOrganizationRequest { Name = "Other", Slug = "POSEIDON" },
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

                    using var patch = new StringContent(
                        "{\"name\":\"Poseidon Labs\",\"brand\":{\"logoUrl\":null,\"primaryColor\":\"#654321\",\"secondaryColor\":null,\"typography\":null}}",
                        Encoding.UTF8,
                        "application/json");
                    using var patchedResponse = await client.PatchAsync(
                        $"/api/v1/organizations/{organizationId}", patch, timeout.Token);
                    Assert.Equal(HttpStatusCode.OK, patchedResponse.StatusCode);
                    var patched = await patchedResponse.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token);
                    Assert.Equal("Poseidon Labs", patched?.Name);
                    Assert.Equal("#654321", patched?.Brand.PrimaryColor);

                    var page = await client.GetFromJsonAsync<OrganizationPage>(
                        "/api/v1/organizations?limit=1", timeout.Token);
                    Assert.Single(page?.Items ?? []);
                    Assert.Null(page?.NextCursor);
                }
                finally
                {
                    await app.StopAsync(timeout.Token);
                }
            }

            await using var restarted = CreateHost(databasePath);
            await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = GetBaseAddress(restarted.Services) };
                var profileId = cookies.GetCookies(client.BaseAddress)["harness.profile"]?.Value;
                Assert.NotNull(profileId);
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                var recovered = await client.GetFromJsonAsync<OrganizationResponse>(
                    $"/api/v1/organizations/{organizationId}", timeout.Token);
                Assert.Equal("Poseidon Labs", recovered?.Name);
                Assert.Equal("#654321", recovered?.Brand.PrimaryColor);
            }
            finally
            {
                await restarted.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static WebApplication CreateHost(string databasePath) =>
        HostApplication.Build(
            [
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", databasePath,
            ]);

    private static Uri GetBaseAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
