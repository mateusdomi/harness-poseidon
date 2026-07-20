using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Profiles;
using Harness.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Identity;

public sealed class PersonalSessionAdoptionTests
{
    [Fact]
    public async Task NewBrowserWithoutCookieAdoptsTheSingleLocalProfileInPersonalMode()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"personal-adopt-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "adopt.db");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);

                // Sem perfil ainda: um navegador sem cookie continua sem sessão (404).
                using (var fresh = new HttpClient { BaseAddress = address })
                using (var beforeAny = await fresh.GetAsync("/api/v1/profiles/current", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.NotFound, beforeAny.StatusCode);
                }

                // Onboarding cria o perfil (com cookie) em um "navegador".
                using (var onboarding = new HttpClient(
                    new HttpClientHandler { CookieContainer = new CookieContainer() })
                { BaseAddress = address })
                {
                    using var created = await onboarding.PostAsJsonAsync(
                        "/api/v1/profiles",
                        new CreateProfileRequest("Mateus", "mateus@example.com", null, "pt-BR"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                }

                // Um navegador DIFERENTE, sem cookie, agora adota o perfil existente.
                using var newBrowser = new HttpClient(
                    new HttpClientHandler { CookieContainer = new CookieContainer() })
                { BaseAddress = address };
                using (var current = await newBrowser.GetAsync("/api/v1/profiles/current", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, current.StatusCode);
                    Assert.True(current.Headers.Contains("Set-Cookie"), "Deveria adotar a sessão gravando o cookie.");
                    var profile = await current.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token);
                    Assert.Equal("Mateus", profile?.DisplayName);
                }

                // E a cascata que quebrava o cockpit deixa de dar 401.
                using (var projects = await newBrowser.GetAsync("/api/v1/projects", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
                }
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
    public async Task MalformedCookieIsReplacedByTheExistingPersonalProfile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"personal-stale-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "stale.db");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);
                using (var onboarding = new HttpClient { BaseAddress = address })
                using (var created = await onboarding.PostAsJsonAsync(
                           "/api/v1/profiles",
                           new CreateProfileRequest("Mateus", null, null, "pt-BR"),
                           timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                }

                using var staleBrowser = new HttpClient { BaseAddress = address };
                staleBrowser.DefaultRequestHeaders.Add(
                    "Cookie", "harness.profile=not-a-valid-ulid");
                using (var current = await staleBrowser.GetAsync(
                           "/api/v1/profiles/current", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, current.StatusCode);
                    Assert.True(current.Headers.Contains("Set-Cookie"));
                    var profile = await current.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token);
                    Assert.Equal("Mateus", profile?.DisplayName);
                }

                using var projects = await staleBrowser.GetAsync("/api/v1/projects", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
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

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
