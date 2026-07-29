using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Projects;
using Harness.Host.Prototyping;
using Harness.Modules.Coordination.Application;
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
    public async Task ProseWithoutBrandAsksTheOwnerInsteadOfInventing()
    {
        await using var host = await StartAsync("prose");
        var project = await host.CreateProjectAsync(withBrand: false);

        var stage = await host.ReadStageAsync(project.Id);

        // Caminho prosa, sem nada herdado: a etapa pende e a Bruna tem o que perguntar.
        Assert.Equal(nameof(PrototypingEntryPath.Prose), stage.EntryPath);
        Assert.Equal(nameof(PrototypingStageState.Pending), stage.State);
        Assert.True(stage.BlocksAdvance);
        Assert.Equal(PrototypingStagePolicy.ReasonPendingIdentity, stage.ReasonCode);
        Assert.Contains(PrototypingIntakePolicy.QuestionBrand, stage.Questions);
        Assert.Empty(stage.Inherited);
        // A mensagem é do dono, não do sistema.
        Assert.Contains("identidade visual", stage.BusinessMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrganizationBrandPlusStoredBundleSatisfiesByInheritanceAndSaysWhatItInherited()
    {
        await using var host = await StartAsync("inherit");
        var project = await host.CreateProjectAsync(withBrand: true);
        await host.StoreBundleReferenceAsync(project.Id);

        var stage = await host.ReadStageAsync(project.Id);

        // Continuidade: quem já tem identidade não é interrogado de novo...
        Assert.Equal(nameof(PrototypingStageState.SatisfiedByInheritance), stage.State);
        Assert.False(stage.BlocksAdvance);
        Assert.Equal(PrototypingStagePolicy.ReasonInherited, stage.ReasonCode);
        Assert.Empty(stage.Questions);
        // ...e a herança é declarada, nunca silenciosa.
        Assert.Contains(PrototypingIntakePolicy.InheritedBrand, stage.Inherited);
        Assert.Contains(PrototypingIntakePolicy.InheritedFromBundle, stage.Inherited);
    }

    [Fact]
    public async Task StoredReactBundleBecomesTheDesignSystemOfTheProject()
    {
        await using var host = await StartAsync("bundle");
        var project = await host.CreateProjectAsync(withBrand: false);
        var reference = await host.StoreBundleReferenceAsync(project.Id);

        var stage = await host.ReadStageAsync(project.Id);

        // O pacote de telas responde a pergunta visual inteira: nada a perguntar.
        Assert.Equal(nameof(PrototypingEntryPath.ReactBundle), stage.EntryPath);
        Assert.Equal(nameof(PrototypingStageState.SatisfiedByInheritance), stage.State);
        Assert.False(stage.BlocksAdvance);
        Assert.Empty(stage.Questions);
        Assert.Contains(PrototypingIntakePolicy.InheritedFromBundle, stage.Inherited);
        Assert.Equal(reference.Id, stage.BundleReferenceId);
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

        /// <summary>Referência visual com origem `bundle`: é assim que o pacote de telas fica no acervo.</summary>
        public async Task<VisualReferenceContract> StoreBundleReferenceAsync(string projectId)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/visual-references",
                new VisualReferenceCreateRequest(
                    projectId,
                    "Telas do pacote enviado",
                    "https://exemplo.test/telas.png",
                    "upload",
                    null,
                    [PrototypingStageEndpoints.DesignSystemTag]));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<VisualReferenceContract>())!;
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

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
