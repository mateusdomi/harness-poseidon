using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Providers;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Modules.Readiness.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.Fake;
using Harness.Modules.Conversations.Contracts;
using Harness.Host.Conversations;

namespace Harness.IntegrationTests.Providers;

/// <summary>
/// Gate do golden path (ADR-018): uma instalação vazia, sem modo demo, não apresenta conta,
/// modelo, cota ou roteamento que o usuário não configurou, e a prontidão reporta o bloqueio
/// de forma tipada em vez de parecer operacional.
/// </summary>
public sealed class EmptyInstallFailClosedTests
{
    [Fact]
    public void NormalPackageHasNoSimulatedAgentExecutor()
    {
        // ADR-019: o executor simulado só é ligado sob demonstração/desenvolvimento explícito.
        // No pacote normal, nenhuma resposta fabricada pode ser apresentada como resposta real.
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"executor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var normal = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", Path.Combine(root, "a.db")]);
            Assert.IsType<UnavailableAgentExecutor>(
                normal.Services.GetRequiredService<IAgentExecutor>());

            using var simulated = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", Path.Combine(root, "b.db"),
                 "--Harness:AgentExecutors:Mode", "simulated"]);
            Assert.IsType<FakeAgentExecutor>(
                simulated.Services.GetRequiredService<IAgentExecutor>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyInstallExposesNoSimulatedAccountsModelsOrBudgetsAndBlocksExecution()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"failclosed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = HostApplication.Build(
                ["--urls", "http://127.0.0.1:0", "--Harness:DatabasePath", Path.Combine(root, "harness.db")]);
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                        .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                };

                using (var created = await client.PostAsJsonAsync(
                    "/api/v1/profiles",
                    new CreateProfileRequest("Mateus", null, null, "pt-BR"),
                    timeout.Token))
                {
                    created.EnsureSuccessStatusCode();
                }

                // Nenhuma conta, modelo, cota ou roteamento fictício em instalação vazia.
                Assert.Empty((await client.GetFromJsonAsync<AccountPage>(
                    "/api/v1/accounts", timeout.Token))!.Items);
                Assert.Empty((await client.GetFromJsonAsync<ModelPage>(
                    "/api/v1/models", timeout.Token))!.Items);
                Assert.Empty((await client.GetFromJsonAsync<BudgetPage>(
                    "/api/v1/budgets", timeout.Token))!.Items);
                Assert.Empty((await client.GetFromJsonAsync<RoutingPolicyPage>(
                    "/api/v1/routing-policies", timeout.Token))!.Items);

                // Os tipos de provider conectáveis existem como ponto de entrada, porém nenhum
                // deles é apresentado como saudável sem credencial configurada.
                var providers = (await client.GetFromJsonAsync<ProviderPage>(
                    "/api/v1/providers", timeout.Token))!;
                Assert.NotEmpty(providers.Items);

                foreach (var resource in new[] { "accounts", "models", "budgets" })
                {
                    var payload = await client.GetStringAsync($"/api/v1/{resource}", timeout.Token);
                    Assert.DoesNotContain("gpt-5", payload, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("OpenAI account", payload, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("Claude Sonnet", payload, StringComparison.OrdinalIgnoreCase);
                }

                // A prontidão reporta o bloqueio de forma tipada, com próxima ação.
                var organization = (await (await client.PostAsJsonAsync(
                    "/api/v1/organizations",
                    new CreateOrganizationRequest { Name = "Poseidon", Slug = "poseidon" },
                    timeout.Token)).Content.ReadFromJsonAsync<OrganizationResponse>(timeout.Token))!;
                var project = (await (await client.PostAsJsonAsync(
                    "/api/v1/projects",
                    new CreateProjectRequest
                    {
                        OrganizationId = organization.Id,
                        Name = "Poseidon",
                        Key = "POSEIDON",
                        Description = "Fail-closed gate.",
                    },
                    timeout.Token)).Content.ReadFromJsonAsync<ProjectResponse>(timeout.Token))!;

                var readiness = (await client.GetFromJsonAsync<ProjectReadinessSnapshot>(
                    $"/api/v1/projects/{project.Id}/readiness", timeout.Token))!;
                var account = readiness.Steps.Single(
                    step => step.Step == ReadinessStep.ProviderAccountReady);
                Assert.Equal(ConfigurationState.Unconfigured, account.State);
                Assert.Equal("provider_account.missing", account.Blockers.Single().Code);
                Assert.Equal("provider.connectAccount", account.NextAction!.Code);

                var model = readiness.Steps.Single(step => step.Step == ReadinessStep.ModelReady);
                Assert.Equal(ConfigurationState.Unconfigured, model.State);

                var execution = readiness.Steps.Single(
                    step => step.Step == ReadinessStep.ExecutionReady);
                Assert.NotEqual(ConfigurationState.Ready, execution.State);
                Assert.NotEmpty(execution.Blockers);
                Assert.NotEqual(ConfigurationState.Ready, readiness.OverallState);
                Assert.NotEmpty(readiness.NextActions);
                // C2/ADR-019: sem provider/modelo o turno NÃO é erro de requisição. A mensagem
                // é persistida e o bloqueio volta tipado, com bloqueadores e próximas ações.
                var conversation = (await (await client.PostAsJsonAsync(
                    "/api/v1/conversations",
                    new CreateConversationRequest(project.Id, "Primeira conversa"),
                    timeout.Token)).Content.ReadFromJsonAsync<ConversationResponse>(timeout.Token))!;
                using (var turn = await client.PostAsJsonAsync(
                    $"/api/v1/conversations/{conversation.Id}/turns",
                    new StartChatTurnRequest("Olá, Chief."),
                    timeout.Token))
                {
                    Assert.Equal(System.Net.HttpStatusCode.Accepted, turn.StatusCode);
                    var handle = (await turn.Content.ReadFromJsonAsync<ChatTurnHandle>(timeout.Token))!;
                    Assert.Equal("blocked", handle.State);
                    Assert.NotEmpty(handle.Blockers);
                    Assert.NotEmpty(handle.NextActions);
                    Assert.NotEmpty(handle.CorrelationId);
                    Assert.Equal(conversation.Id, handle.ConversationId);
                    Assert.Equal(
                        $"/api/v1/projects/{project.Id}/readiness", handle.Links.Readiness);
                    Assert.All(handle.Blockers,
                        blocker => Assert.False(string.IsNullOrWhiteSpace(blocker.Code)));
                }

                // A mensagem humana foi persistida mesmo com a execução bloqueada, e o retry
                // é idempotente: nenhuma mensagem, turno ou evento duplicado.
                var afterFirst = (await client.GetFromJsonAsync<MessagePage>(
                    $"/api/v1/messages?conversationId={conversation.Id}", timeout.Token))!;
                Assert.Single(afterFirst.Items);

                // Perfil, organização e projeto são reais porque de fato existem. Já as etapas
                // que dependem de configuração externa não podem aparecer como execução real —
                // nem como simulada, já que o modo demo está desligado.
                Assert.All(
                    readiness.Steps.Where(step => step.Step
                        is ReadinessStep.ProviderAccountReady
                        or ReadinessStep.ModelReady
                        or ReadinessStep.ChiefDefinitionReady
                        or ReadinessStep.ExecutionReady),
                    step => Assert.Equal("unconfigured", step.ExecutionMode));
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
