using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Workflows;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Workflows.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Workflows;

/// <summary>
/// DEL-07: o workflow de ENTREGA TÉCNICA de 11 fases é semeado e publicado como template canônico
/// (reusa o módulo de Workflows / GP-09), aparecendo no catálogo com suas 11 fases e seus portões por
/// fase. Se o seeding das fases (incl. objetivos-documento) falhasse, o template nem apareceria.
/// </summary>
public sealed class TechnicalDeliveryWorkflowSeedTests
{
    [Fact]
    public async Task TechnicalDeliveryTemplateIsSeededWithElevenPhasesAndGates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"tech-delivery-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "tech-delivery.db");
        Directory.CreateDirectory(root);
        var cookies = new CookieContainer();

        try
        {
            await using var app = CreateHost(database);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = cookies };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                await CreateProfileAsync(client, timeout.Token);

                var templates = (await client.GetFromJsonAsync<WorkflowTemplatePage>(
                    "/api/v1/workflow-templates?limit=50", timeout.Token))!;
                var technical = templates.Items.Single(template =>
                    template.Name.StartsWith("Entrega técnica", StringComparison.Ordinal));

                var version = (await client.GetFromJsonAsync<WorkflowVersionContract>(
                    $"/api/v1/workflow-versions/{technical.CurrentVersionId}", timeout.Token))!;

                Assert.Equal(
                    [
                        "Recebimento", "Baseline", "Planejamento", "Execução acompanhada", "Prontidão homolog",
                        "Homologação", "Prontidão prod", "Produção", "Estabilização", "Encerramento",
                        "Revisão de benefícios",
                    ],
                    version.Phases);

                // Oito fases de decisão/prontidão têm portão de aprovação.
                Assert.Equal(8, version.GatesByPhase.Count);
                Assert.Contains("Homologação", version.GatesByPhase.Keys);
                Assert.Contains("Produção", version.GatesByPhase.Keys);
                Assert.Contains("Encerramento", version.GatesByPhase.Keys);
            }
            finally { await app.StopAsync(timeout.Token); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task CreateProfileAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/profiles",
            new CreateProfileRequest("Mateus", null, null, "pt-BR"), token);
        response.EnsureSuccessStatusCode();
    }

    private static WebApplication CreateHost(string database) => HostApplication.Build(
        ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", database]);

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(x => x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
