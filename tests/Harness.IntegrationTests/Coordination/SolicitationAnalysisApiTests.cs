using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Coordination;

public sealed class SolicitationAnalysisApiTests
{
    [Fact]
    public async Task AnalysisCreatesImmutableSolicitationAndSurvivesRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"analysis-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "analysis.db"); Directory.CreateDirectory(root);
        var cookies = new CookieContainer(); string profileId; string solicitationId;
        try
        {
            await using (var app = CreateHost(database))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.PostAsJsonAsync("/api/v1/solicitations/analyze",
                        new AnalyzeSolicitationRequest("01ARZ3NDEKTSV4RRFFQ69G5FAV", "Pedido"), timeout.Token))
                        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    using var handler = new HttpClientHandler { CookieContainer = cookies };
                    using var client = new HttpClient(handler) { BaseAddress = address };
                    using (var response = await client.PostAsJsonAsync("/api/v1/profiles",
                        new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                    { response.EnsureSuccessStatusCode(); profileId = (await response.Content.ReadFromJsonAsync<ProfileResponse>(timeout.Token))!.Id; }
                    string organizationId;
                    using (var response = await client.PostAsJsonAsync("/api/v1/organizations",
                        new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token))
                    { response.EnsureSuccessStatusCode(); organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id; }
                    string projectId;
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects",
                        new CreateProjectRequest { OrganizationId = organizationId, Name = "Analyzer", Key = "ANALYZER", Description = "PO" }, timeout.Token))
                    { response.EnsureSuccessStatusCode(); projectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!.Id; }

                    using (var invalid = await client.PostAsJsonAsync("/api/v1/solicitations/analyze",
                        new AnalyzeSolicitationRequest(projectId, "Pedido", ["../escape.zip"]), timeout.Token))
                        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                    using var analyzedResponse = await client.PostAsJsonAsync("/api/v1/solicitations/analyze",
                        new AnalyzeSolicitationRequest(projectId,
                            "Criar busca rápida. Talvez aceite filtros. Qual volume esperado?",
                            ["requisitos.pdf", "layout.png"]), timeout.Token);
                    Assert.Equal(HttpStatusCode.Created, analyzedResponse.StatusCode);
                    var analysis = (await analyzedResponse.Content.ReadFromJsonAsync<SolicitationAnalysisContract>(timeout.Token))!;
                    solicitationId = analysis.SolicitationId;
                    Assert.Equal(4, analysis.Requirements.Count);
                    Assert.Single(analysis.Ambiguities);
                    Assert.Empty(analysis.Contradictions);
                    Assert.Contains(analysis.Questions, item => item.Text == "Qual volume esperado?");
                    Assert.Equal(analysis.Requirements.Count, analysis.AcceptanceCriteria.Count);
                    Assert.All(analysis.Requirements.Concat(analysis.Ambiguities)
                        .Concat(analysis.Questions).Concat(analysis.AcceptanceCriteria),
                        item => Assert.Equal(26, item.Id.Length));
                    var solicitation = await client.GetFromJsonAsync<SolicitationContract>(
                        $"/api/v1/solicitations/{solicitationId}", timeout.Token);
                    Assert.Equal(profileId, solicitation?.AuthorProfileId);
                    Assert.Equal("request", solicitation?.Kind);
                    Assert.Equal("open", solicitation?.State);
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await using var restarted = CreateHost(database); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                using var response = await client.GetAsync($"/api/v1/solicitations/{solicitationId}", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
