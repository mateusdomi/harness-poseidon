using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Realtime;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Projects;

public sealed class ProjectApiTests
{
    [Fact]
    public async Task ProjectCrudPersistsVersionedConfigurationAndCreatedEventAcrossRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"project-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "project.db");
        Directory.CreateDirectory(artifactRoot);
        var cookies = new CookieContainer();
        string profileId;
        string projectId;

        try
        {
            await using (var app = CreateHost(databasePath))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = GetBaseAddress(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var unauthorized = await anonymous.GetAsync("/api/v1/projects", timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                    }

                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using var profileResponse = await client.PostAsJsonAsync(
                        "/api/v1/profiles",
                        new CreateProfileRequest("Mateus", null, null, "pt-BR"),
                        timeout.Token);
                    var profile = await profileResponse.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token);
                    Assert.NotNull(profile);
                    profileId = profile.Id;

                    using var organizationResponse = await client.PostAsJsonAsync(
                        "/api/v1/organizations",
                        new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
                        timeout.Token);
                    var organization = await organizationResponse.Content
                        .ReadFromJsonAsync<OrganizationResponse>(timeout.Token);
                    Assert.NotNull(organization);

                    using var missingOrganization = await client.PostAsJsonAsync(
                        "/api/v1/projects",
                        Request("01ARZ3NDEKTSV4RRFFQ69G5FAV", "MISSING"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.NotFound, missingOrganization.StatusCode);

                    using var createdResponse = await client.PostAsJsonAsync(
                        "/api/v1/projects",
                        Request(organization.Id, "POSEIDON"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
                    var created = await createdResponse.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                    Assert.NotNull(created);
                    projectId = created.Id;
                    Assert.Equal("active", created.State);
                    Assert.Equal("manual", created.OperationMode);
                    Assert.Equal([profileId], created.MemberProfileIds);
                    Assert.Equal(1, created.ConfigVersion);

                    using var duplicate = await client.PostAsJsonAsync(
                        "/api/v1/projects",
                        Request(organization.Id, "poseidon"),
                        timeout.Token);
                    Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

                    using var rename = new StringContent(
                        "{\"name\":\"Poseidon Labs\"}", Encoding.UTF8, "application/json");
                    using var renamedResponse = await client.PatchAsync(
                        $"/api/v1/projects/{projectId}", rename, timeout.Token);
                    var renamed = await renamedResponse.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                    Assert.Equal(1, renamed?.ConfigVersion);

                    using var configure = new StringContent(
                        "{\"repositoryUrl\":\"https://github.com/example/poseidon\",\"repositoryProvider\":\"github\",\"technologies\":[\".NET\",\"React\"]}",
                        Encoding.UTF8,
                        "application/json");
                    using var configuredResponse = await client.PatchAsync(
                        $"/api/v1/projects/{projectId}", configure, timeout.Token);
                    var configured = await configuredResponse.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token);
                    Assert.Equal(2, configured?.ConfigVersion);
                    Assert.Equal([".NET", "React"], configured?.Technologies);

                    var page = await client.GetFromJsonAsync<ProjectPage>(
                        "/api/v1/projects?limit=1", timeout.Token);
                    Assert.Single(page?.Items ?? []);

                    var snapshot = await WaitForCreatedEventAsync(client, projectId, timeout.Token);
                    Assert.Equal("project.created", Assert.Single(snapshot.Delta).Type);
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
                var recovered = await client.GetFromJsonAsync<ProjectResponse>(
                    $"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal("Poseidon Labs", recovered?.Name);
                Assert.Equal(2, recovered?.ConfigVersion);

                using var deleted = await client.DeleteAsync($"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                using var missing = await client.GetAsync($"/api/v1/projects/{projectId}", timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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

    private static CreateProjectRequest Request(string organizationId, string key) =>
        new()
        {
            OrganizationId = organizationId,
            Name = $"Project {key}",
            Key = key,
            Description = "Backend project",
            Criticality = "high",
            DefaultBranch = "develop",
            Technologies = [".NET", "SQLite"],
        };

    private static async Task<EventStreamSnapshot> WaitForCreatedEventAsync(
        HttpClient client,
        string projectId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = await client.GetFromJsonAsync<EventStreamSnapshot>(
                $"/api/v1/event-streams/snapshot?stream=project:{projectId}",
                cancellationToken);
            if (snapshot is { Delta.Count: > 0 })
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException("project.created was not dispatched.");
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
