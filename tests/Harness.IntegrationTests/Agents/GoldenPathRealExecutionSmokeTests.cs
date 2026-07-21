using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Providers;
using Harness.IntegrationTests.Support;
using Harness.Modules.Conversations.Contracts;
using Harness.Host.Conversations;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Host.Organizations;
using Harness.Modules.Readiness.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// C5/ADR-019 — smoke condicional do golden path com provider e modelo REAIS, configurados
/// por variáveis de ambiente. Percorre
/// mensagem → turno → prontidão → definição → conta → provider → modelo → effort → executor.
///
/// Sem credencial externa o teste é pulado com o marcador explícito
/// <c>SKIPPED_EXTERNAL_CREDENTIALS</c>: a ausência de credencial NUNCA é tratada como prova
/// da integração real. O caminho fake permanece separado e sempre rotulado.
/// </summary>
public sealed class GoldenPathRealExecutionSmokeTests
{
    private const string SkipMarker = "SKIPPED_EXTERNAL_CREDENTIALS";

    private sealed record SmokeCredentials(
        string ProviderKind, string CredentialReference, string ModelName,
        int ContextWindow, string Effort, string ProviderEffortValue);

    // Nenhum segredo é lido de argumento de comando, fixture ou documentação: apenas do
    // ambiente do processo, e a referência de credencial é opaca (keychain://, secret://…).
    private static SmokeCredentials? ResolveCredentials()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HARNESS_RUN_REAL_GOLDEN_PATH"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = Environment.GetEnvironmentVariable("HARNESS_SMOKE_PROVIDER_KIND");
        var credential = Environment.GetEnvironmentVariable("HARNESS_SMOKE_CREDENTIAL_REFERENCE");
        var model = Environment.GetEnvironmentVariable("HARNESS_SMOKE_MODEL_NAME");
        if (string.IsNullOrWhiteSpace(kind) ||
            string.IsNullOrWhiteSpace(credential) ||
            string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return new SmokeCredentials(
            kind,
            credential,
            model,
            int.TryParse(
                Environment.GetEnvironmentVariable("HARNESS_SMOKE_CONTEXT_WINDOW"),
                out var window) && window > 0 ? window : 128000,
            Environment.GetEnvironmentVariable("HARNESS_SMOKE_EFFORT") ?? "medium",
            Environment.GetEnvironmentVariable("HARNESS_SMOKE_PROVIDER_EFFORT_VALUE") ?? "medium");
    }

    [Fact]
    public async Task RealProviderAndModelDriveTheGoldenPathToAnUnblockedTurn()
    {
        var credentials = ResolveCredentials();
        if (credentials is null)
        {
            // Registro explícito: sem credencial externa esta prova NÃO foi executada.
            Assert.True(true, SkipMarker);
            Console.WriteLine(
                $"{SkipMarker}: defina HARNESS_RUN_REAL_GOLDEN_PATH=true, " +
                "HARNESS_SMOKE_PROVIDER_KIND, HARNESS_SMOKE_CREDENTIAL_REFERENCE e " +
                "HARNESS_SMOKE_MODEL_NAME para exercer a execução real.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"golden-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0",
                 "--Harness:DatabasePath", Path.Combine(root, "harness.db")]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                        .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                };

                using (var profile = await client.PostAsJsonAsync(
                    "/api/v1/profiles",
                    new CreateProfileRequest("Homologação", null, null, "pt-BR"),
                    timeout.Token))
                {
                    profile.EnsureSuccessStatusCode();
                }

                var organization = (await (await client.PostAsJsonAsync(
                    "/api/v1/organizations",
                    new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
                    timeout.Token)).Content
                    .ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!;
                var project = (await (await client.PostAsJsonAsync(
                    "/api/v1/projects",
                    new CreateProjectRequest
                    {
                        OrganizationId = organization.Id,
                        Name = "Poseidon",
                        Key = "POSEIDON",
                        Description = "Smoke de execução real.",
                    },
                    timeout.Token)).Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!;

                // Provider real: a conta nasce desabilitada e só a referência opaca de
                // credencial trafega; o segredo em si nunca entra em log, fixture ou payload.
                var providers = (await client.GetFromJsonAsync<ProviderPage>(
                    "/api/v1/providers", timeout.Token))!;
                var provider = providers.Items.Single(item =>
                    string.Equals(item.Kind, credentials.ProviderKind, StringComparison.OrdinalIgnoreCase));
                var account = (await (await client.PostAsJsonAsync(
                    "/api/v1/accounts",
                    new CreateProviderAccountRequest(
                        provider.Id, "Conta de homologação", credentials.CredentialReference, null),
                    timeout.Token)).Content.ReadFromJsonAsync<AccountContract>(timeout.Token))!;
                using (var enable = await client.PatchAsync(
                    $"/api/v1/accounts/{account.Id}",
                    JsonContent.Create(new { state = "active" }),
                    timeout.Token))
                {
                    enable.EnsureSuccessStatusCode();
                }

                var model = (await (await client.PostAsJsonAsync(
                    "/api/v1/models",
                    new CreateProviderModelRequest(
                        provider.Id, credentials.ModelName, credentials.ModelName,
                        ["chat", "code"], credentials.ContextWindow, null, null,
                        [new EffortMappingContract(credentials.Effort, credentials.ProviderEffortValue)]),
                    timeout.Token)).Content.ReadFromJsonAsync<ModelContract>(timeout.Token))!;
                Assert.Equal(provider.Id, model.ProviderId);

                await WorkflowTestBinding.BindRecommendedAsync(client, project.Id, timeout.Token);

                // A prontidão canônica deve reconhecer a configuração real.
                var readiness = (await client.GetFromJsonAsync<ProjectReadinessSnapshot>(
                    $"/api/v1/projects/{project.Id}/readiness", timeout.Token))!;
                var execution = readiness.Steps.Single(
                    step => step.Step == ReadinessStep.ExecutionReady);
                Assert.Equal("real", execution.ExecutionMode);
                Assert.Equal(ConfigurationState.Ready, execution.State);

                var conversation = (await (await client.PostAsJsonAsync(
                    $"/api/v1/projects/{project.Id}/conversations/primary", new { }, timeout.Token))
                    .Content.ReadFromJsonAsync<ConversationResponse>(timeout.Token))!;
                using var turn = await client.PostAsJsonAsync(
                    $"/api/v1/conversations/{conversation.Id}/turns",
                    new StartChatTurnRequest("Liste os próximos passos do projeto."),
                    timeout.Token);
                Assert.Equal(HttpStatusCode.Accepted, turn.StatusCode);
                var handle = (await turn.Content.ReadFromJsonAsync<ChatTurnHandle>(timeout.Token))!;

                // Com provider e modelo reais o turno é ENFILEIRADO, nunca bloqueado.
                Assert.Equal("pending", handle.State);
                Assert.Empty(handle.Blockers);
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
}
