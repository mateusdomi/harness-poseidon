using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Harness.Host;
using Harness.Host.Governance;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Prototyping;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Prototyping;

public sealed class VisualReferenceAssetApiTests
{
    [Fact]
    public async Task UploadsImagesAndZipsForReferencesWhileRejectingDangerousAssets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"reference-assets-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "assets.db");
        var cookies = new CookieContainer();
        Directory.CreateDirectory(root);

        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                await CreateProfileAsync(client, timeout.Token);
                var organization = await CreateOrganizationAsync(client, timeout.Token);
                var project = await CreateProjectAsync(client, organization.Id, timeout.Token);
                using var referenceResponse = await client.PostAsJsonAsync(
                    "/api/v1/visual-references",
                    new VisualReferenceCreateRequest(
                        project.Id,
                        "Paleta do cockpit",
                        "https://example.local/reference.png",
                        "upload",
                        null,
                        ["cockpit"]),
                    timeout.Token);
                referenceResponse.EnsureSuccessStatusCode();
                var reference = (await referenceResponse.Content
                    .ReadFromJsonAsync<VisualReferenceContract>(timeout.Token))!;

                byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03];
                using (var upload = Multipart("paleta.png", "image/png", png))
                using (var response = await client.PostAsync(
                    $"/api/v1/visual-references/{reference.Id}/assets", upload, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var contract = (await response.Content
                        .ReadFromJsonAsync<VisualReferenceAssetContract>(timeout.Token))!;
                    Assert.Equal("paleta.png", contract.FileName);
                }

                using (var zipUpload = Multipart("referencias.zip", "application/zip", BuildZip(
                    ("mock-1.png", png),
                    ("notas.md", Encoding.UTF8.GetBytes("# Notas de referência visual")))))
                using (var response = await client.PostAsync(
                    $"/api/v1/visual-references/{reference.Id}/assets", zipUpload, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                }

                using (var pdf = Multipart("mock.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-1.7")))
                using (var response = await client.PostAsync(
                    $"/api/v1/visual-references/{reference.Id}/assets", pdf, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                }

                using (var sneaky = Multipart("tema.zip", "application/zip", BuildZip(
                    ("instalador.txt", [0x4D, 0x5A, 0x00, 0x01]))))
                using (var response = await client.PostAsync(
                    $"/api/v1/visual-references/{reference.Id}/assets", sneaky, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                }

                using (var duplicate = Multipart("paleta.png", "image/png", png))
                using (var response = await client.PostAsync(
                    $"/api/v1/visual-references/{reference.Id}/assets", duplicate, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                }

                var assets = (await client.GetFromJsonAsync<VisualReferenceAssetPage>(
                    $"/api/v1/visual-references/{reference.Id}/assets", timeout.Token))!;
                Assert.Equal(2, assets.Items.Count);

                var accepted = (await client.GetFromJsonAsync<AuditEventPage>(
                    "/api/v1/audit-events?action=prototype.assetAccepted&limit=50", timeout.Token))!;
                Assert.Equal(2, accepted.Items.Count);
                var rejected = (await client.GetFromJsonAsync<AuditEventPage>(
                    "/api/v1/audit-events?action=prototype.assetRejected&limit=50", timeout.Token))!;
                Assert.Equal(2, rejected.Items.Count);
                Assert.All(rejected.Items, item => Assert.Equal(reference.Id, item.TargetId));
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
                Directory.Delete(root, true);
            }
        }
    }

    private static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static MultipartFormDataContent Multipart(string fileName, string contentType, byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new MultipartFormDataContent { { content, "file", fileName } };
    }

    private static async Task<ProfileResponse> CreateProfileAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"),
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(token))!;
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(
        HttpClient client,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(token))!;
    }

    private static async Task<ProjectResponse> CreateProjectAsync(
        HttpClient client,
        string organizationId,
        CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "POSEIDON",
                Description = "Backend",
            },
            token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(token))!;
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
