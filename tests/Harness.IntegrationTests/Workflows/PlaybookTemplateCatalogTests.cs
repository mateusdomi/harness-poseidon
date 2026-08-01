using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Modules.Documents.Contracts;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Workflows;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.IntegrationTests.Workflows;

/// <summary>
/// Fase 2A.1 — o catálogo de templates do playbook deixou de ser genérico, e os campos passaram a
/// governar de fato.
///
/// A tríade <c>["titulo","objetivo","conteudo"]</c> valia para qualquer coisa e por isso não guiava
/// coisa nenhuma: "produza o template 11 (GMUD)" tinha como especificação três palavras que não
/// distinguem uma GMUD de um comunicado de férias.
/// </summary>
public sealed class PlaybookTemplateCatalogTests
{
    [Fact]
    public async Task EveryTemplateDeclaresItsOwnFieldsAndDocumentCreationEnforcesThem()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"templates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build([
                "--urls", "http://127.0.0.1:0",
                "--Harness:DatabasePath", Path.Combine(root, "templates.db"),
            ]);
            await app.StartAsync(timeout.Token);
            try
            {
                var templates = await app.Services
                    .GetRequiredService<IWorkflowDocumentTemplateStore>()
                    .ListAsync(timeout.Token);

                Assert.NotEmpty(templates);

                // PROVA do gate: nenhum template sobrou com a tríade genérica.
                var generic = templates
                    .Where(item => item.RequiredFieldsJson.Contains("\"conteudo\"", StringComparison.Ordinal))
                    .Select(item => item.Code)
                    .ToArray();
                Assert.Empty(generic);

                // Todo template diz o que precisa PROVAR — orientação vazia devolve o agente ao
                // mesmo vazio de antes, só que com campos diferentes.
                Assert.DoesNotContain(templates, item => string.IsNullOrWhiteSpace(item.Guidance));

                // Os campos declarados são específicos: a GMUD exige rollback TESTADO, e o
                // postmortem exige linha do tempo — nenhum dos dois vale para o outro.
                var gmud = templates.Single(item => item.Code == "11");
                Assert.Contains("plano_rollback_testado", gmud.RequiredFieldsJson, StringComparison.Ordinal);
                var postmortem = templates.Single(item => item.Code == "13b");
                Assert.Contains("linha_tempo", postmortem.RequiredFieldsJson, StringComparison.Ordinal);

                // Onde há métrica, o FORMATO é fixo — número sem unidade declarada é opinião.
                Assert.Contains("lead_time", templates.Single(item => item.Code == "16").MetricFormatsJson,
                    StringComparison.Ordinal);

                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
                (await client.PostAsJsonAsync("/api/v1/profiles",
                    new CreateProfileRequest("Operador", null, null, "pt-BR"), timeout.Token))
                    .EnsureSuccessStatusCode();
                var orgId = await PostId(client, "/api/v1/organizations",
                    new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" }, timeout.Token);
                var projectId = await PostId(client, "/api/v1/projects",
                    new CreateProjectRequest
                    {
                        OrganizationId = orgId,
                        Name = "Poseidon",
                        Key = "PSD",
                        Description = "Control plane",
                    }, timeout.Token);

                // O `kind` do documento ('spec') e o `target_card_type` do template ('documento')
                // são vocabulários DISTINTOS que ainda não conversam — achado registrado no
                // relatório da Fase 2A. A verificação de template independe dessa unificação.
                //
                // RECUSA: uma GMUD sem plano de rollback testado não entra no acervo.
                using (var refused = await client.PostAsJsonAsync("/api/v1/documents",
                    new CreateDocumentRequest(
                        projectId, "GMUD da release 1.4", "spec",
                        "## Janela\nMadrugada.\n\n## Plano de execução\nSubir tudo.\n\n" +
                        "## Critérios de saúde\nNinguém reclamar.\n\n## Aprovador\nMateus.\n",
                        TemplateCode: "11"),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                    var problem = await refused.Content.ReadAsStringAsync(timeout.Token);
                    Assert.Contains("plano_rollback_testado", problem, StringComparison.Ordinal);
                }

                // ACEITA: a mesma GMUD com a seção que faltava, prosa livre.
                using (var accepted = await client.PostAsJsonAsync("/api/v1/documents",
                    new CreateDocumentRequest(
                        projectId, "GMUD da release 1.4", "spec",
                        "## Janela\nMadrugada de terça.\n\n## Plano de execução\nSubir a migração, " +
                        "depois o serviço.\n\n## Plano de rollback testado\nEnsaiado em 01/08: 6 min.\n\n" +
                        "## Critérios de saúde\nErro 5xx abaixo de 0,1% por 30 min.\n\n## Aprovador\nMateus.\n",
                        TemplateCode: "11"),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
                }

                // Template inexistente é recusado explicitamente, não ignorado em silêncio: um
                // código errado que passasse batido devolveria o documento sem verificação nenhuma.
                using (var unknown = await client.PostAsJsonAsync("/api/v1/documents",
                    new CreateDocumentRequest(
                        projectId, "Documento", "spec", "## Qualquer\ncoisa\n",
                        TemplateCode: "9z"),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
                }

                // Sem template declarado o documento é LIVRE: nem todo documento de um projeto é
                // um artefato do playbook.
                using (var free = await client.PostAsJsonAsync("/api/v1/documents",
                    new CreateDocumentRequest(
                        projectId, "Nota de reunião", "note", "Texto solto, sem seções.\n"),
                    timeout.Token))
                {
                    Assert.Equal(HttpStatusCode.Created, free.StatusCode);
                }
            }
            finally
            {
                await app.StopAsync(timeout.Token);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // limpeza best-effort
            }
        }
    }

    private static async Task<string> PostId<T>(HttpClient client, string route, T body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(route, body, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("id").GetString()!;
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
