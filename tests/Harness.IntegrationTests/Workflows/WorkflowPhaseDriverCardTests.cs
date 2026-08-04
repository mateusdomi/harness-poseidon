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
using Harness.Persistence.Abstractions.Workflows;
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
            Assert.Equal("documento", card.CardType);
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

            var deadlineMessageId = UlidValue.New(now.AddMinutes(1)).ToString();
            var deadlineMessage = await conversations.CreateMessageAsync(
                new MessageCreateCommand(
                    localProfile.TenantId,
                    new MessageRecord(
                        localProfile.TenantId, projectContract.Id, deadlineMessageId,
                        conversationId, "user", profile.Id, null,
                        "Não tenho prazo fixo. Pode seguir.", null, now.AddMinutes(1)),
                    now.AddMinutes(1)),
                timeout.Token);
            Assert.Equal(MessageMutationStatus.Applied, deadlineMessage.Status);
            var obsoleteTaskId = UlidValue.New(now.AddMinutes(1).AddMilliseconds(1)).ToString();
            var obsoleteTitle =
                $"1-Triagem — Ficha de Demanda Qualificada — atualização {deadlineMessageId}";
            _ = await board.CreateTaskAsync(
                new BoardTaskCreateCommand(
                    localProfile.TenantId, obsoleteTaskId, project.Id, demandId,
                    UlidValue.New(now.AddMinutes(1).AddMilliseconds(2)).ToString(),
                    solicitationId, profile.Id, obsoleteTitle, "low", null, null,
                    UlidValue.New(now.AddMinutes(1).AddMilliseconds(3)).ToString(),
                    "Atualização documental que não possui delta material.",
                    now.AddMinutes(1).AddMilliseconds(1), "1-Triagem", "documento"),
                timeout.Token);
            _ = await board.MoveTaskAsync(
                new BoardTaskMoveCommand(
                    localProfile.TenantId, obsoleteTaskId, "ready", null, "system",
                    now.AddMinutes(1).AddMilliseconds(4)),
                timeout.Token);

            _ = await driver.DriveAsync(
                localProfile.TenantId, project, profile.Id, timeout.Token);

            var dismissed = await board.GetTaskAsync(
                localProfile.TenantId, obsoleteTaskId, timeout.Token);
            Assert.Equal("done", dismissed?.State);
            Assert.Equal("cancelled", dismissed?.InternalState);
            Assert.NotNull(dismissed?.ArchivedAt);
            Assert.Contains("não altera este artefato", dismissed?.BlockedReason,
                StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// AUSÊNCIA NÃO É OBSERVAÇÃO. O casamento card↔obrigação é por título, sobre a página do quadro
    /// ATIVO. Quando o card é arquivado — ou apenas cai fora da página —, ele some do casamento; e a
    /// reconciliação escrevia `pending` por isso, apagando um `accepted` conquistado. Como a criação
    /// de card só dispara com o OBJETIVO ainda pendente, e ele já tinha avançado, nenhum card nascia
    /// para reconquistar o degrau: o portão passava a esperar para sempre por um trabalho que já fora
    /// entregue e aceito. Medido no projeto 01KYZHMGZASV0A1G0QM9RB248M, parado na fase 2 com o
    /// objetivo em `validated` e a obrigação em `pending`. As fases 6 a 9 são inteiramente de
    /// documento: uma obrigação regredida ali é a operação inteira.
    /// </summary>
    [Fact]
    public async Task ArchivingTheCardDoesNotDemoteAnAcceptedObligation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"phase-oblig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var app = HostApplication.Build([
            "--urls", "http://127.0.0.1:0",
            "--Harness:DatabasePath", Path.Combine(root, "phase-oblig.db"),
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
            var obligations = app.Services.GetRequiredService<IPhaseObligationStore>();
            var catalog = app.Services.GetRequiredService<IWorkflowCatalogStore>();
            using var scope = app.Services.CreateScope();
            var project = (await scope.ServiceProvider.GetRequiredService<IProjectStore>()
                .GetAsync(localProfile.TenantId, projectContract.Id, timeout.Token))!;
            var driver = scope.ServiceProvider.GetRequiredService<WorkflowPhaseDriver>();

            var now = DateTimeOffset.UtcNow;
            var conversationId = UlidValue.New(now.AddMilliseconds(-2)).ToString();
            var messageId = UlidValue.New(now.AddMilliseconds(-1)).ToString();
            _ = await conversations.CreateConversationAsync(
                new ConversationCreateCommand(
                    new ConversationRecord(
                        localProfile.TenantId, conversationId, projectContract.Id, "Conversa inicial",
                        "active", profile.Id, now.AddMilliseconds(-2), null, 1),
                    now.AddMilliseconds(-2)),
                timeout.Token);
            _ = await conversations.CreateMessageAsync(
                new MessageCreateCommand(
                    localProfile.TenantId,
                    new MessageRecord(
                        localProfile.TenantId, projectContract.Id, messageId, conversationId,
                        "user", profile.Id, null,
                        "Quero aviso antes da renovação.", null, now.AddMilliseconds(-1)),
                    now.AddMilliseconds(-1)),
                timeout.Token);
            var solicitationId = UlidValue.New(now).ToString();
            var demandId = UlidValue.New(now.AddMilliseconds(1)).ToString();
            _ = await board.CreateSolicitationAsync(
                new BoardSolicitationCreateCommand(
                    localProfile.TenantId, solicitationId, projectContract.Id, profile.Id, "request",
                    "Controlar assinaturas", "Quero aviso antes da renovação.", null, now),
                timeout.Token);
            _ = await board.CreateDemandAsync(
                new BoardDemandCreateCommand(
                    localProfile.TenantId, demandId, projectContract.Id, solicitationId,
                    solicitationId, profile.Id, "Avisar antes da renovação",
                    "A antecedência é premissa explícita.", "medium", now.AddMilliseconds(1)),
                timeout.Token);

            _ = await driver.DriveAsync(localProfile.TenantId, project, profile.Id, timeout.Token);

            var card = Assert.Single(
                await board.ListTasksAsync(
                    localProfile.TenantId, project.Id, null, null, 100, timeout.Token),
                task => task.Title == "1-Triagem — Ficha de Demanda Qualificada");

            // O degrau conquistado: a obrigação daquele artefato está ACEITA.
            var binding = (await catalog.ListBindingsAsync(
                localProfile.TenantId, project.Id, null, 1, timeout.Token))[0];
            var run = (await catalog.ListRunsAsync(
                localProfile.TenantId, binding.Id, null, 20, timeout.Token))
                .Single(item => item.State == "running");
            var current = await obligations.ListCurrentAsync(
                localProfile.TenantId, run.Id, "phase-1", timeout.Token);
            var target = current.Single(item =>
                item.Description.Contains("Ficha de Demanda Qualificada", StringComparison.Ordinal));
            Assert.True(await obligations.UpdateStateAsync(
                new PhaseObligationStateCommand(
                    localProfile.TenantId, target.ObligationId, "accepted",
                    [$"card:{card.Id}"], null, DateTimeOffset.UtcNow),
                timeout.Token));

            // O card sai do quadro ATIVO. Uso o mesmo caminho que a produção usa — o encerramento
            // sem entrega, que arquiva —, que é exatamente o estado do card no projeto travado
            // (`cancelled` com `archived_at`). Cair fora da página tem efeito idêntico no
            // casamento por título.
            var dismissed = await board.DismissTaskAsync(
                new BoardTaskDismissCommand(
                    localProfile.TenantId, card.Id,
                    "Card encerrado depois de o artefato já ter sido aceito.",
                    "system", DateTimeOffset.UtcNow),
                timeout.Token);
            Assert.NotNull(dismissed.ArchivedAt);

            _ = await driver.DriveAsync(localProfile.TenantId, project, profile.Id, timeout.Token);

            var after = (await obligations.ListCurrentAsync(
                localProfile.TenantId, run.Id, "phase-1", timeout.Token))
                .Single(item => item.ObligationId == target.ObligationId);
            Assert.Equal("accepted", after.State);
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
