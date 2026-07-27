using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Governance.Memory;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.SharedKernel.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Governance;

/// <summary>
/// Fase 6 — memória semântica no fluxo REAL: o anexo aceito pelo intake é indexado no índice
/// vetorial derivado com proveniência, e a busca devolve slices com citação, score e o snapshot
/// do Context Builder (hash + orçamento de tokens) — carga de contexto auditável, montada
/// server-side. O índice nunca é fonte da verdade: cada slice cita o anexo durável de origem.
/// </summary>
public sealed class MemorySearchApiTests
{
    [Fact]
    public async Task AcceptedAttachmentsBecomeSearchableMemoryWithProvenanceAndAuditableSnapshot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"memory-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "memory.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };

                var projectId = await SeedProjectAsync(client, timeout.Token);
                var solicitationId = await SeedSolicitationAsync(client, projectId, timeout.Token);

                // Dois anexos reais com conteúdos distintos: a busca precisa DISCRIMINAR.
                await UploadAsync(
                    client, solicitationId, "relatorio-auditoria.md",
                    "# Relatório\n\nExportar auditoria em CSV com filtro por data e paginação.",
                    timeout.Token);
                await UploadAsync(
                    client, solicitationId, "identidade-visual.md",
                    "# Identidade\n\nPaleta de cores, tipografia e logotipo da marca Poseidon.",
                    timeout.Token);

                // Documento de outro projeto é deliberadamente a combinação perfeita. Se o
                // filtro ocorrer somente depois do topK, ele rouba uma vaga do projeto pedido.
                var profile = Assert.Single(
                    await app.Services.GetRequiredService<ILocalProfileStore>()
                        .ListAsync(timeout.Token));
                const string competingProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
                const string competingContent = "exportar auditoria csv filtro data";
                await app.Services.GetRequiredService<IVectorIndex>().IndexAsync(
                    new VectorDocumentRecord(
                        "competing-other-project",
                        profile.TenantId,
                        competingProjectId,
                        "solicitation_attachment",
                        competingContent,
                        DeterministicLocalEmbedding.Embed(competingContent),
                        new Dictionary<string, string>
                        {
                            ["fileName"] = "competidor.md",
                            ["solicitationId"] = "other-project",
                        },
                        DateTimeOffset.UtcNow),
                    timeout.Token);

                using var response = await client.GetAsync(
                    $"/api/v1/governance-runtime/memory-search?projectId={projectId}" +
                    "&query=exportar%20auditoria%20csv%20filtro%20data&topK=2",
                    timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(timeout.Token));

                // Snapshot auditável do Context Builder: id + hash + tokens contabilizados.
                Assert.False(string.IsNullOrWhiteSpace(
                    body.RootElement.GetProperty("snapshotId").GetString()));
                Assert.Matches(
                    "^[0-9a-f]{64}$",
                    body.RootElement.GetProperty("snapshotHash").GetString()!);
                Assert.True(body.RootElement.GetProperty("totalTokens").GetInt32() > 0);

                var slices = body.RootElement.GetProperty("slices").EnumerateArray().ToArray();
                Assert.Equal(2, slices.Length);

                // O slice mais relevante é o do relatório — a busca discriminou por conteúdo.
                var top = slices[0];
                Assert.Contains(
                    "auditoria",
                    top.GetProperty("content").GetString()!,
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(
                    top.GetProperty("score").GetDouble() > slices[1].GetProperty("score").GetDouble(),
                    "O slice do relatório precisa pontuar acima do slice de identidade visual.");

                // Proveniência completa: o slice cita o anexo durável de origem.
                Assert.Equal("solicitation_attachment", top.GetProperty("documentType").GetString());
                Assert.Equal(projectId, top.GetProperty("projectId").GetString());
                Assert.StartsWith(
                    "solicitation_attachment:",
                    top.GetProperty("citationReference").GetString(),
                    StringComparison.Ordinal);
                var provenance = top.GetProperty("provenance");
                Assert.Equal(solicitationId, provenance.GetProperty("solicitationId").GetString());
                Assert.Equal(
                    "relatorio-auditoria.md",
                    provenance.GetProperty("fileName").GetString());
                Assert.Matches("^[0-9A-F]{64}$", provenance.GetProperty("sha256").GetString()!);

                // Query vazia é inválida — a memória não devolve "tudo" por omissão.
                using var invalid = await client.GetAsync(
                    "/api/v1/governance-runtime/memory-search?projectId=" + projectId,
                    timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
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

    private static async Task UploadAsync(
        HttpClient client,
        string solicitationId,
        string fileName,
        string body,
        CancellationToken token)
    {
        var payload = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        payload.Headers.ContentType = MediaTypeHeaderValue.Parse("text/markdown");
        using var upload = new MultipartFormDataContent { { payload, "file", fileName } };
        using var response = await client.PostAsync(
            $"/api/v1/solicitations/{solicitationId}/attachments", upload, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string> SeedSolicitationAsync(
        HttpClient client, string projectId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/solicitations",
            new CreateSolicitationRequest(
                projectId, "request", "Memória de anexos", "Insumos para a memória semântica."),
            token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> SeedProjectAsync(HttpClient client, CancellationToken token)
    {
        using var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles",
            new CreateProfileRequest("Operador", null, null, "pt-BR"),
            token);
        profile.EnsureSuccessStatusCode();

        using var organization = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
            token);
        organization.EnsureSuccessStatusCode();
        using var organizationBody = JsonDocument.Parse(
            await organization.Content.ReadAsStringAsync(token));
        var organizationId = organizationBody.RootElement.GetProperty("id").GetString()!;

        using var project = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest
            {
                OrganizationId = organizationId,
                Name = "Poseidon",
                Key = "PSD",
                Description = "Projeto da memória semântica.",
            },
            token);
        project.EnsureSuccessStatusCode();
        using var projectBody = JsonDocument.Parse(
            await project.Content.ReadAsStringAsync(token));
        return projectBody.RootElement.GetProperty("id").GetString()!;
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish an address.");
        return new Uri(addresses.Single(item =>
            item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
