using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Projects;
using Harness.Host.Prototyping;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Prototyping;

/// <summary>
/// D11 — a etapa de Prototipação vista pelo produto, um cenário por CAMINHO DE ENTRADA.
///
/// As políticas eram puras e ninguém as chamava: o produto não sabia dizer se a etapa estava
/// satisfeita, por herança ou por aprovação, nem o que ainda faltava perguntar. Estes cenários
/// leem o estado pelo endpoint real, com dados reais, e fixam a regra que mais importa: herdar
/// nunca acontece em silêncio — o que veio da organização aparece na resposta.
/// </summary>
public sealed class PrototypingStageApiTests
{
    [Fact]
    public async Task EachEntryPathSatisfiesTheStageInItsOwnWay()
    {
        // UM host para os tres caminhos: cada cenario e um projeto distinto. Tres hosts
        // completos custavam o triplo e sobrecarregavam vizinhos sensiveis a tempo na
        // mesma suite (o POC de Postgres perdia a conexao).
        await using var host = await StartAsync("entry-paths");

        /* ---- caminho 1: prosa, sem nada herdado ---- */
        var prose = await host.CreateProjectAsync(withBrand: false);
        var stage = await host.ReadStageAsync(prose.Id);

        // Caminho prosa, sem nada herdado: a etapa pende e a Bruna tem o que perguntar.
        Assert.Equal(nameof(PrototypingEntryPath.Prose), stage.EntryPath);
        Assert.Equal(nameof(PrototypingStageState.Pending), stage.State);
        Assert.True(stage.BlocksAdvance);
        Assert.Equal(PrototypingStagePolicy.ReasonPendingIdentity, stage.ReasonCode);
        Assert.Contains(PrototypingIntakePolicy.QuestionBrand, stage.Questions);
        Assert.Empty(stage.Inherited);
        // A mensagem é do dono, não do sistema.
        Assert.Contains("identidade visual", stage.BusinessMessage, StringComparison.OrdinalIgnoreCase);

        /* ---- caminho 2: documento real de requisitos + protótipo publicado ---- */
        var specification = await host.CreateProjectAsync(withBrand: true);
        await host.UploadRequirementsAsync(specification.Id);
        await host.PublishPrototypeAsync(specification.Id);
        stage = await host.ReadStageAsync(specification.Id);

        Assert.Equal(nameof(PrototypingEntryPath.RequirementsDocument), stage.EntryPath);
        Assert.Equal(nameof(PrototypingStageState.SatisfiedByApproval), stage.State);
        Assert.False(stage.BlocksAdvance);
        Assert.Equal(PrototypingStagePolicy.ReasonApproved, stage.ReasonCode);
        Assert.Contains(PrototypingIntakePolicy.InheritedBrand, stage.Inherited);

        /* ---- caminho 3: pacote React real vira o design system do projeto ---- */
        var bundleOnly = await host.CreateProjectAsync(withBrand: false);
        var reference = await host.UploadBundleAsync(bundleOnly.Id);
        stage = await host.ReadStageAsync(bundleOnly.Id);

        // O pacote de telas responde a pergunta visual inteira: nada a perguntar.
        Assert.Equal(nameof(PrototypingEntryPath.ReactBundle), stage.EntryPath);
        Assert.Equal(nameof(PrototypingStageState.SatisfiedByInheritance), stage.State);
        Assert.False(stage.BlocksAdvance);
        Assert.Empty(stage.Questions);
        Assert.Contains(PrototypingIntakePolicy.InheritedFromBundle, stage.Inherited);
        Assert.Equal(reference.ReferenceId, stage.BundleReferenceId);

        /* ---- o mesmo pacote no intake inicial também é promovido ---- */
        var intakeBundle = await host.CreateProjectAsync(withBrand: false);
        await host.UploadBundleViaIntakeAsync(intakeBundle.Id);
        stage = await host.ReadStageAsync(intakeBundle.Id);
        Assert.Equal(nameof(PrototypingEntryPath.ReactBundle), stage.EntryPath);
        Assert.Equal(nameof(PrototypingStageState.SatisfiedByInheritance), stage.State);
        Assert.NotNull(stage.BundleReferenceId);

        using var invalid = Multipart(
            "anotacoes.zip",
            "application/zip",
            BuildZip(("leia-me.txt", Encoding.UTF8.GetBytes("sem telas"))));
        using var rejected = await host.Client.PostAsync(
            $"/api/v1/projects/{bundleOnly.Id}/design-system-bundle",
            invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Contains("Não encontrei as telas", problem!.Detail, StringComparison.Ordinal);
    }

    /* ---- harness ---- */

    private static async Task<StageHost> StartAsync(string label)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"prototyping-stage-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var app = HostApplication.Build(
            ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", Path.Combine(root, "stage.db")]);
        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = Address(app.Services),
        };
        using (var profile = await client.PostAsJsonAsync(
            "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR")))
        {
            profile.EnsureSuccessStatusCode();
        }

        return new StageHost(app, client, root);
    }

    private sealed class StageHost(WebApplication app, HttpClient client, string root) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public async Task<ProjectResponse> CreateProjectAsync(bool withBrand)
        {
            string organizationId;
            using (var response = await client.PostAsJsonAsync(
                "/api/v1/organizations",
                new CreateOrganizationRequest
                {
                    Name = $"Org {Guid.NewGuid():N}"[..12],
                    Slug = $"org-{Guid.NewGuid():N}"[..12],
                    Brand = withBrand
                        ? new OrganizationBrandContract("https://exemplo.test/logo.png", "#123456", null, null)
                        : null,
                }))
            {
                response.EnsureSuccessStatusCode();
                organizationId = (await response.Content.ReadFromJsonAsync<OrganizationResponse>())!.Id;
            }

            using var created = await client.PostAsJsonAsync(
                "/api/v1/projects",
                new CreateProjectRequest
                {
                    OrganizationId = organizationId,
                    Name = "Loja",
                    Key = $"LOJA{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                    Description = "Projeto de teste da etapa de prototipação.",
                });
            created.EnsureSuccessStatusCode();
            return (await created.Content.ReadFromJsonAsync<ProjectResponse>())!;
        }

        public async Task UploadRequirementsAsync(string projectId)
        {
            using var solicitationResponse = await client.PostAsJsonAsync(
                "/api/v1/solicitations",
                new CreateSolicitationRequest(
                    projectId,
                    "request",
                    "Aplicativo da loja",
                    "As telas e os fluxos estão no documento anexo."));
            solicitationResponse.EnsureSuccessStatusCode();
            var solicitation = (await solicitationResponse.Content
                .ReadFromJsonAsync<SolicitationContract>())!;
            using var upload = Multipart(
                "requisitos-frontend.md",
                "text/markdown",
                Encoding.UTF8.GetBytes("# Telas\n- Catálogo\n- Carrinho\n- Pagamento"));
            using var response = await client.PostAsync(
                $"/api/v1/solicitations/{solicitation.Id}/attachments",
                upload);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        public async Task PublishPrototypeAsync(string projectId)
        {
            using var created = await client.PostAsJsonAsync(
                "/api/v1/prototypes",
                new PrototypeCreateRequest(projectId, "Fluxo da loja", "Protótipo navegável.", null));
            created.EnsureSuccessStatusCode();
            var prototype = (await created.Content.ReadFromJsonAsync<PrototypeContract>())!;
            using (var ready = await client.PostAsJsonAsync(
                $"/api/v1/prototypes/{prototype.Id}/transitions",
                new PrototypeTransitionRequest("ready", null, null)))
            {
                ready.EnsureSuccessStatusCode();
            }
            using var published = await client.PostAsJsonAsync(
                $"/api/v1/prototypes/{prototype.Id}/transitions",
                new PrototypeTransitionRequest(
                    "published",
                    "https://example.test/prototipo",
                    null));
            published.EnsureSuccessStatusCode();
        }

        public async Task<DesignSystemBundleContract> UploadBundleAsync(string projectId)
        {
            using var upload = Multipart(
                "loja-react.zip",
                "application/zip",
                BuildZip(
                    ("package.json", Encoding.UTF8.GetBytes("{\"scripts\":{\"build\":\"vite build\"}}")),
                    ("src/App.tsx", Encoding.UTF8.GetBytes("export function App(){return <main>Loja</main>}"))));
            using var response = await client.PostAsync(
                $"/api/v1/projects/{projectId}/design-system-bundle",
                upload);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<DesignSystemBundleContract>())!;
        }

        public async Task UploadBundleViaIntakeAsync(string projectId)
        {
            using var solicitationResponse = await client.PostAsJsonAsync(
                "/api/v1/solicitations",
                new CreateSolicitationRequest(
                    projectId,
                    "request",
                    "Pacote inicial da loja",
                    "As telas prontas seguem anexas."));
            solicitationResponse.EnsureSuccessStatusCode();
            var solicitation = (await solicitationResponse.Content
                .ReadFromJsonAsync<SolicitationContract>())!;
            using var upload = Multipart(
                "telas-iniciais.zip",
                "application/zip",
                BuildZip(
                    ("package.json", Encoding.UTF8.GetBytes("{\"dependencies\":{\"react\":\"latest\"}}")),
                    ("src/App.tsx", Encoding.UTF8.GetBytes("export const App=()=> <main />"))));
            using var response = await client.PostAsync(
                $"/api/v1/solicitations/{solicitation.Id}/attachments",
                upload);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        public async Task<PrototypingStageContract> ReadStageAsync(string projectId)
        {
            var stage = await client.GetFromJsonAsync<PrototypingStageContract>(
                $"/api/v1/projects/{projectId}/prototyping-stage");
            Assert.NotNull(stage);
            return stage;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
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

    private static MultipartFormDataContent Multipart(
        string fileName,
        string contentType,
        byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new MultipartFormDataContent { { content, "file", fileName } };
    }

    private sealed record ProblemPayload(string? Title, string? Detail);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
