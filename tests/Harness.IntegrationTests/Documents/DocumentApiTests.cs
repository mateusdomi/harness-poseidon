using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Documents;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Modules.Documents.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Documents;

public sealed class DocumentApiTests
{
    [Fact]
    public async Task DocumentsAndImmutableBodiesSurviveHostRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"document-api-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "document.db"); var catalog = Path.Combine(root, "catalog");
        Directory.CreateDirectory(root); var cookies = new CookieContainer();
        string profileId; string projectId; string documentId; string firstVersionId; string secondVersionId;
        try
        {
            await using (var app = CreateHost(database, catalog))
            {
                await app.StartAsync(timeout.Token);
                try
                {
                    var address = Address(app.Services);
                    using (var anonymous = new HttpClient { BaseAddress = address })
                    using (var denied = await anonymous.GetAsync("/api/v1/documents", timeout.Token))
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
                    using (var response = await client.PostAsJsonAsync("/api/v1/projects",
                        new CreateProjectRequest { OrganizationId = organizationId, Name = "Poseidon", Key = "POSEIDON", Description = "Backend" }, timeout.Token))
                    { response.EnsureSuccessStatusCode(); projectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!.Id; }

                    using (var response = await client.PostAsJsonAsync("/api/v1/documents",
                        new CreateDocumentRequest(projectId, "ADR 001", "spec", "# v1", ["ux", "arquitetura"], null), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var document = await response.Content.ReadFromJsonAsync<DocumentContract>(timeout.Token);
                        Assert.NotNull(document); documentId = document.Id;
                        Assert.Equal("inElaboration", document.State); Assert.Equal(["arquitetura", "ux"], document.Classifications);
                        Assert.Null(document.PhaseName); Assert.Null(document.Waiver); Assert.Equal(1, document.CurrentVersion);
                    }
                    var page = await client.GetFromJsonAsync<DocumentPage>($"/api/v1/documents?projectId={projectId}", timeout.Token);
                    Assert.Equal(documentId, Assert.Single(page!.Items).Id);
                    var versions = await client.GetFromJsonAsync<DocumentVersionPage>($"/api/v1/document-versions?documentId={documentId}", timeout.Token);
                    var first = Assert.Single(versions!.Items); firstVersionId = first.Id;
                    Assert.Equal("# v1", first.Body); Assert.Equal("user", first.AuthorKind); Assert.Equal(profileId, first.AuthorId);

                    using (var response = await client.PostAsJsonAsync("/api/v1/document-versions",
                        new CreateDocumentVersionRequest(documentId, "# v2\n\nCorrigido."), timeout.Token))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var version = await response.Content.ReadFromJsonAsync<DocumentVersionContract>(timeout.Token);
                        Assert.NotNull(version); secondVersionId = version.Id; Assert.Equal(2, version.Version); Assert.Equal("# v2\n\nCorrigido.", version.Body);
                    }
                    versions = await client.GetFromJsonAsync<DocumentVersionPage>($"/api/v1/document-versions?documentId={documentId}", timeout.Token);
                    Assert.Equal(["# v1", "# v2\n\nCorrigido."], versions!.Items.OrderBy(value => value.Version).Select(value => value.Body));
                    Assert.Equal(2, (await client.GetFromJsonAsync<DocumentContract>($"/api/v1/documents/{documentId}", timeout.Token))!.CurrentVersion);
                }
                finally { await app.StopAsync(timeout.Token); }
            }

            await using var restarted = CreateHost(database, catalog); await restarted.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = Address(restarted.Services) };
                client.DefaultRequestHeaders.Add("Cookie", $"harness.profile={profileId}");
                Assert.Equal("# v1", (await client.GetFromJsonAsync<DocumentVersionContract>($"/api/v1/document-versions/{firstVersionId}", timeout.Token))!.Body);
                Assert.Equal("# v2\n\nCorrigido.", (await client.GetFromJsonAsync<DocumentVersionContract>($"/api/v1/document-versions/{secondVersionId}", timeout.Token))!.Body);
                Assert.Equal(2, (await client.GetFromJsonAsync<DocumentContract>($"/api/v1/documents/{documentId}", timeout.Token))!.CurrentVersion);
            }
            finally { await restarted.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static WebApplication CreateHost(string database, string catalog) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database,
         "--Harness:DocumentCatalogPath", catalog]);
    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
