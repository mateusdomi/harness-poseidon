using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Harness.Host;
using Harness.Host.Governance;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.WorkBoard;

public sealed class SolicitationAttachmentApiTests
{
    [Fact]
    public async Task StorageRejectsNonUlidSegmentsAndResolveTraversal()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"attachment-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var storage = new SolicitationAttachmentStorage(root);
            var valid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
            await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(
                "../tenant", valid, new byte[] { 1 }, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(
                valid, "../attachment", new byte[] { 1 }, CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => storage.Resolve("../outside"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptsRealDocumentRejectsMaliciousUploadsAndFeedsDemandCreation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"attachments-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "attachments.db");
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
                using var solicitationResponse = await client.PostAsJsonAsync(
                    "/api/v1/solicitations",
                    new CreateSolicitationRequest(
                        project.Id,
                        "request",
                        "Requisitos do relatório",
                        "Documento anexo descreve o relatório de auditoria."),
                    timeout.Token);
                solicitationResponse.EnsureSuccessStatusCode();
                var solicitation = (await solicitationResponse.Content
                    .ReadFromJsonAsync<SolicitationContract>(timeout.Token))!;

                // Documento real aceito, com hash verificável e arquivo persistido.
                var documentBytes = Encoding.UTF8.GetBytes(
                    "# Relatório de auditoria\n\nRequisito 1: exportar CSV.\nRequisito 2: filtro por data.\n");
                using (var upload = Multipart("requisitos.md", "text/markdown", documentBytes))
                using (var response = await client.PostAsync(
                    $"/api/v1/solicitations/{solicitation.Id}/attachments", upload, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var contract = (await response.Content
                        .ReadFromJsonAsync<SolicitationAttachmentContract>(timeout.Token))!;
                    Assert.Equal("requisitos.md", contract.FileName);
                    Assert.Equal("accepted", contract.State);
                    Assert.Equal(
                        Convert.ToHexString(SHA256.HashData(documentBytes)),
                        contract.Sha256);
                    var storedRoot = Path.Combine(Path.GetDirectoryName(database)!, "attachments");
                    Assert.True(Directory.Exists(storedRoot));
                    Assert.Single(
                        Directory.EnumerateFiles(storedRoot, "*", SearchOption.AllDirectories));
                }

                using (var duplicate = Multipart("requisitos.md", "text/markdown", documentBytes))
                using (var response = await client.PostAsync(
                    $"/api/v1/solicitations/{solicitation.Id}/attachments", duplicate, timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                }

                // Uploads maliciosos rejeitados com o código fechado correspondente.
                foreach (var (fileName, contentType, payload, expectedCode) in Malicious())
                {
                    using var upload = Multipart(fileName, contentType, payload);
                    using var response = await client.PostAsync(
                        $"/api/v1/solicitations/{solicitation.Id}/attachments", upload, timeout.Token);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(timeout.Token);
                    Assert.Equal(expectedCode, problem!.Title);
                }

                var attachments = (await client.GetFromJsonAsync<SolicitationAttachmentPage>(
                    $"/api/v1/solicitations/{solicitation.Id}/attachments", timeout.Token))!;
                var attachment = Assert.Single(attachments.Items);
                Assert.Equal("requisitos.md", attachment.FileName);

                var rejectedAudit = (await client.GetFromJsonAsync<AuditEventPage>(
                    "/api/v1/audit-events?action=solicitation.attachmentRejected&limit=50",
                    timeout.Token))!;
                Assert.Equal(3, rejectedAudit.Items.Count);
                Assert.All(rejectedAudit.Items, item => Assert.Equal(solicitation.Id, item.TargetId));
                var acceptedAudit = (await client.GetFromJsonAsync<AuditEventPage>(
                    "/api/v1/audit-events?action=solicitation.attachmentAccepted&limit=50",
                    timeout.Token))!;
                Assert.Single(acceptedAudit.Items);

                // Demanda criada a partir da solicitação com documento real anexado.
                using var demandResponse = await client.PostAsJsonAsync(
                    "/api/v1/demands",
                    new CreateDemandRequest(
                        project.Id,
                        "Exportar relatório de auditoria",
                        "Implementar exportação CSV com filtro por data, conforme requisitos.md.",
                        solicitation.Id),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Created, demandResponse.StatusCode);
                var demand = (await demandResponse.Content
                    .ReadFromJsonAsync<DemandContract>(timeout.Token))!;
                Assert.Equal(solicitation.Id, demand.SolicitationId);
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

    private static IEnumerable<(string FileName, string ContentType, byte[] Payload, string Code)> Malicious()
    {
        yield return ("../evil.md", "text/markdown", Encoding.UTF8.GetBytes("x"), "path_traversal");
        yield return ("relatorio.pdf", "application/pdf", [0x4D, 0x5A, 0x90, 0x00], "executable_content");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("zeros.txt", CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            entryStream.Write(new byte[8 * 1024 * 1024]);
        }

        yield return ("bomba.zip", "application/zip", stream.ToArray(), "zip_bomb");
    }

    private static MultipartFormDataContent Multipart(string fileName, string contentType, byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new MultipartFormDataContent { { content, "file", fileName } };
    }

    private sealed record ProblemPayload(string Title, string? Detail);

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
