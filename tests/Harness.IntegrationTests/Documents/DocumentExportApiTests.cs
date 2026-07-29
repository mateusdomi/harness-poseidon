using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Documents;
using Harness.Host.Organizations;
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

/// <summary>
/// O dono baixa a produção da equipe para levar a um sócio, cliente ou
/// auditoria. O que este teste protege é a honestidade do pacote: sai o que foi
/// aprovado, com a versão que saiu registrada, e nada além disso.
/// </summary>
public sealed class DocumentExportApiTests
{
    [Fact]
    public async Task ApprovedDocumentsLeaveInAZipWithAnHonestManifest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"document-export-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "export.db");
        var catalog = Path.Combine(root, "catalog");
        Directory.CreateDirectory(root);

        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database,
                 "--Harness:DocumentCatalogPath", catalog]);
            await app.StartAsync(timeout.Token);
            try
            {
                var address = Address(app.Services);

                using (var anonymous = new HttpClient { BaseAddress = address })
                using (var denied = await anonymous.GetAsync(
                    "/api/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/documents/export", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                }

                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = address };

                using (var response = await client.PostAsJsonAsync("/api/v1/profiles",
                    new CreateProfileRequest("Mateus", null, null, "pt-BR"), timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                }

                string organizationId;
                using (var response = await client.PostAsJsonAsync("/api/v1/organizations",
                    new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    organizationId = (await response.Content
                        .ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!.Id;
                }

                string projectId, chiefAgentId;
                using (var response = await client.PostAsJsonAsync("/api/v1/projects",
                    new CreateProjectRequest
                    {
                        OrganizationId = organizationId,
                        Name = "Loja da Ana",
                        Key = "LOJA",
                        Description = "Catalogo online",
                    }, timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!;
                    projectId = project.Id;
                    chiefAgentId = project.ChiefAgentId;
                }

                // ID que não é ULID e projeto inexistente falham antes de qualquer leitura.
                using (var invalid = await client.GetAsync(
                    "/api/v1/projects/nao-e-ulid/documents/export", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                }
                using (var missing = await client.GetAsync(
                    "/api/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/documents/export", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                }

                var approvedId = await CreateDocumentAsync(
                    client, projectId, "Visão do produto", "# Visão\n\nO que a loja precisa ser.", timeout.Token);
                var draftId = await CreateDocumentAsync(
                    client, projectId, "Rascunho interno", "# Rascunho\n\nAinda mudando.", timeout.Token);
                await ApproveAsync(client, projectId, chiefAgentId, approvedId, timeout.Token);

                byte[] archive;
                using (var download = await client.GetAsync(
                    $"/api/v1/projects/{projectId}/documents/export", timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                    Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
                    archive = await download.Content.ReadAsByteArrayAsync(timeout.Token);
                }

                using var buffer = new MemoryStream(archive);
                using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
                var names = zip.Entries.Select(entry => entry.FullName).ToArray();

                // O aprovado sai; o rascunho não — e o manifesto acompanha.
                Assert.Contains("manifesto.json", names);
                Assert.Contains(names, name => name.Contains("visao-do-produto", StringComparison.Ordinal));
                Assert.DoesNotContain(names, name => name.Contains("rascunho", StringComparison.Ordinal));
                Assert.Equal(2, names.Length);

                var documentEntry = zip.Entries.Single(entry => entry.FullName != "manifesto.json");
                using (var stream = new StreamReader(documentEntry.Open()))
                {
                    Assert.Contains("O que a loja precisa ser.",
                        await stream.ReadToEndAsync(timeout.Token), StringComparison.Ordinal);
                }

                using var manifestReader = new StreamReader(
                    zip.Entries.Single(entry => entry.FullName == "manifesto.json").Open());
                using var manifest = JsonDocument.Parse(await manifestReader.ReadToEndAsync(timeout.Token));
                var manifestRoot = manifest.RootElement;
                Assert.Equal(projectId, manifestRoot.GetProperty("projectId").GetString());
                Assert.Equal(1, manifestRoot.GetProperty("documentCount").GetInt32());
                var entry = manifestRoot.GetProperty("documents").EnumerateArray().Single();
                Assert.Equal(approvedId, entry.GetProperty("documentId").GetString());
                Assert.Equal("Visão do produto", entry.GetProperty("title").GetString());
                Assert.Equal(1, entry.GetProperty("version").GetInt32());
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("contentHash").GetString()));
                Assert.Equal(documentEntry.FullName, entry.GetProperty("path").GetString());

                // O rascunho segue existindo no projeto: ele foi excluído do
                // pacote, não do produto.
                var page = await client.GetFromJsonAsync<DocumentPage>(
                    $"/api/v1/documents?projectId={projectId}", timeout.Token);
                Assert.Contains(page!.Items, item => item.Id == draftId);
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CreateDocumentAsync(
        HttpClient client, string projectId, string title, string body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/documents",
            new CreateDocumentRequest(projectId, title, "prd", body, ["produto"], null), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentContract>(token))!.Id;
    }

    private static async Task ApproveAsync(
        HttpClient client, string projectId, string chiefAgentId, string documentId, CancellationToken token)
    {
        using (var review = await client.PostAsJsonAsync($"/api/v1/documents/{documentId}/transitions",
            new TransitionDocumentRequest("inReview"), token))
        {
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }

        string approvalId;
        using (var request = await client.PostAsJsonAsync("/api/v1/approvals",
            new CreateApprovalRequest(projectId, "Aprovar documento", "Revisão do dono", chiefAgentId,
                DocumentId: documentId), token))
        {
            Assert.Equal(HttpStatusCode.Created, request.StatusCode);
            approvalId = (await request.Content.ReadFromJsonAsync<ApprovalContract>(token))!.Id;
        }

        using var approve = await client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/resolution",
            new ResolveApprovalRequest("approved", "Aceito."), token);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var document = await client.GetFromJsonAsync<DocumentContract>(
            $"/api/v1/documents/{documentId}", token);
        Assert.Equal("approved", document!.State);
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
