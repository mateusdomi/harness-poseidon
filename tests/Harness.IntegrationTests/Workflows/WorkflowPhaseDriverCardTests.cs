using System.Net;
using System.Net.Http.Json;
using Harness.Host;
using Harness.Host.Organizations;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Workflows;
using Harness.Modules.Identity.Contracts;
using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.IntegrationTests.Workflows;

public sealed class WorkflowPhaseDriverCardTests
{
    [Fact]
    public async Task FirstPlaybookCardPersistsHumanProvenancePersonaAndTraceability()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"phase-card-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(root, "phase-card.db"),
            "--Harness:AgentRuns:Enabled=true",
            "--Harness:AgentRuns:ControlledRoot", Path.Combine(root, "worktrees"),
            "--Harness:AgentRuns:ProfilesRoot", Path.Combine(root, "profiles"),
            "--Harness:AgentRuns:AvailabilityLedgerPath", Path.Combine(root, "availability.json"),
            "--Harness:AgentRuns:ArchiveRoot", Path.Combine(root, "archive"),
            "--Harness:AgentRuns:AutoDispatchEnabled=false",
        ]);
        try
        {
            await app.StartAsync(timeout.Token);
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var client = new HttpClient(handler) { BaseAddress = Address(app.Services) };
            using var profileResponse = await client.PostAsJsonAsync(
                "/api/v1/profiles", new CreateProfileRequest("Mateus", null, null, "pt-BR"),
                timeout.Token);
            profileResponse.EnsureSuccessStatusCode();
            var profile = (await profileResponse.Content.ReadFromJsonAsync<ProfileResponse>(
                timeout.Token))!;
            using var organizationResponse = await client.PostAsJsonAsync(
                "/api/v1/organizations",
                new CreateOrganizationRequest { Name = "QA", Slug = "qa" }, timeout.Token);
            organizationResponse.EnsureSuccessStatusCode();
            var organization = (await organizationResponse.Content.ReadFromJsonAsync<OrganizationResponse>(
                timeout.Token))!;
            using var projectResponse = await client.PostAsJsonAsync(
                "/api/v1/projects",
                new CreateProjectRequest
                {
                    OrganizationId = organization.Id,
                    Name = "Assinaturas",
                    Key = "ASSINATURAS",
                    Description = "Evitar renovações indesejadas.",
                },
                timeout.Token);
            projectResponse.EnsureSuccessStatusCode();
            var projectContract = (await projectResponse.Content.ReadFromJsonAsync<ProjectResponse>(
                timeout.Token))!;
            var localProfile = (await app.Services.GetRequiredService<ILocalProfileStore>()
                .GetAsync(profile.Id, timeout.Token))!;
            var board = app.Services.GetRequiredService<IWorkBoardStore>();
            var conversations = app.Services.GetRequiredService<IConversationStore>();
            using var scope = app.Services.CreateScope();
            var project = (await scope.ServiceProvider.GetRequiredService<IProjectStore>()
                .GetAsync(localProfile.TenantId, projectContract.Id, timeout.Token))!;
            var driver = scope.ServiceProvider.GetRequiredService<WorkflowPhaseDriver>();

            var beforeKickoff = await driver.DriveAsync(
                localProfile.TenantId, project, profile.Id, timeout.Token);
            Assert.Equal(0, beforeKickoff.CardsCreated);
            Assert.Empty(await board.ListTasksAsync(
                localProfile.TenantId, project.Id, null, null, 100, timeout.Token));

            var now = DateTimeOffset.UtcNow;
            var conversationId = UlidValue.New(now.AddMilliseconds(-2)).ToString();
            var messageId = UlidValue.New(now.AddMilliseconds(-1)).ToString();
            var createdConversation = await conversations.CreateConversationAsync(
                new ConversationCreateCommand(
                    new ConversationRecord(
                        localProfile.TenantId, conversationId, projectContract.Id, "Conversa inicial",
                        "active", profile.Id, now.AddMilliseconds(-2), null, 1),
                    now.AddMilliseconds(-2)),
                timeout.Token);
            Assert.Equal(ConversationMutationStatus.Applied, createdConversation.Status);
            var createdMessage = await conversations.CreateMessageAsync(
                new MessageCreateCommand(
                    localProfile.TenantId,
                    new MessageRecord(
                        localProfile.TenantId, projectContract.Id, messageId, conversationId,
                        "user", profile.Id, null,
                        "Quero aviso antes da renovação para não pagar algo que não queria.",
                        null, now.AddMilliseconds(-1)),
                    now.AddMilliseconds(-1)),
                timeout.Token);
            Assert.Equal(MessageMutationStatus.Applied, createdMessage.Status);
            var solicitationId = UlidValue.New(now).ToString();
            var demandId = UlidValue.New(now.AddMilliseconds(1)).ToString();
            _ = await board.CreateSolicitationAsync(
                new BoardSolicitationCreateCommand(
                    localProfile.TenantId, solicitationId, projectContract.Id, profile.Id, "request",
                    "Controlar assinaturas",
                    "Quero aviso antes da renovação; não sei quantos dias, decida você.", null, now),
                timeout.Token);
            _ = await board.CreateDemandAsync(
                new BoardDemandCreateCommand(
                    localProfile.TenantId, demandId, projectContract.Id, solicitationId,
                    solicitationId, profile.Id, "Avisar antes da renovação",
                    "A antecedência deve ser uma premissa explícita se não estiver definida.",
                    "medium", now.AddMilliseconds(1)),
                timeout.Token);

            var result = await driver.DriveAsync(
                localProfile.TenantId, project, profile.Id, timeout.Token);

            Assert.True(result.CardsCreated > 0);
            var card = Assert.Single(
                await board.ListTasksAsync(
                    localProfile.TenantId, project.Id, null, null, 100, timeout.Token),
                task => task.Title == "1-Triagem — Ficha de Demanda Qualificada");
            Assert.Equal(demandId, card.DemandId);
            Assert.Equal("1-Triagem", card.PhaseName);
            var instruction = Assert.Single(await board.ListInstructionsAsync(
                localProfile.TenantId, card.Id, null, 10, timeout.Token));
            Assert.Contains(
                "Especialidade exigida: playbook-product-owner", instruction.Body,
                StringComparison.Ordinal);
            Assert.Contains($"mensagem:{messageId}", instruction.Body, StringComparison.Ordinal);
            Assert.Contains("não pagar algo que não queria", instruction.Body, StringComparison.Ordinal);
            Assert.Contains($"solicitação:{solicitationId}", instruction.Body, StringComparison.Ordinal);
            Assert.Contains($"demanda:{demandId}", instruction.Body, StringComparison.Ordinal);
            Assert.Contains("PREMISSA INFERIDA", instruction.Body, StringComparison.Ordinal);
            Assert.Contains("Evidências obrigatórias", instruction.Body, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Uri Address(IServiceProvider services)
    {
        var addresses = services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No address.");
        return new Uri(addresses.Single(value => value.StartsWith(
            "http://127.0.0.1:", StringComparison.Ordinal)));
    }
}
