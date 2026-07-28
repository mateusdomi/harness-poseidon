using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harness.Host;
using Harness.Host.Profiles;
using Harness.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Identity;

public sealed class LocalProfileApiTests
{
    [Fact]
    public async Task PersonalProfilePersistsSessionAndPatchAcrossHostRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"local-profile-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "profile.db");
        Directory.CreateDirectory(artifactRoot);
        string profileId;

        try
        {
            var cookies = new CookieContainer();
            await using (var app = CreateHost(databasePath))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = GetBaseAddress(app.Services);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using var missing = await client.GetAsync("/api/v1/profiles/current", timeout.Token);
                    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                    Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);

                    using var createdResponse = await client.PostAsJsonAsync(
                        "/api/v1/profiles",
                        new CreateProfileRequest("Mateus", "mateus@example.com", null, "pt-BR"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
                    var created = await createdResponse.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token);
                    Assert.NotNull(created);
                    profileId = created.Id;
                    Assert.Equal("Mateus", created.DisplayName);
                    Assert.Equal("admin", created.Role);
                    Assert.Contains(
                        "HttpOnly",
                        createdResponse.Headers.GetValues("Set-Cookie").Single(),
                        StringComparison.OrdinalIgnoreCase);

                    var current = await client.GetFromJsonAsync<ProfileResponse>(
                        "/api/v1/profiles/current",
                        timeout.Token);
                    Assert.Equal(profileId, current?.Id);
                    Assert.Equal("admin", current?.Role);

                    using var patch = new StringContent(
                        "{\"displayName\":\"Mateus Domi\",\"email\":null}",
                        Encoding.UTF8,
                        "application/json");
                    using var patchedResponse = await client.PatchAsync(
                        $"/api/v1/profiles/{profileId}",
                        patch,
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.OK, patchedResponse.StatusCode);
                    var patched = await patchedResponse.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token);
                    Assert.Equal("Mateus Domi", patched?.DisplayName);
                    Assert.Null(patched?.Email);

                    var page = await client.GetFromJsonAsync<ProfilePage>("/api/v1/profiles?limit=1", timeout.Token);
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
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                var current = await client.GetFromJsonAsync<ProfileResponse>(
                    "/api/v1/profiles/current",
                    timeout.Token);
                Assert.Equal("Mateus Domi", current?.DisplayName);

                using var duplicate = await client.PostAsJsonAsync(
                    "/api/v1/profiles",
                    new CreateProfileRequest("Outro", null, null, "pt-BR"),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
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
