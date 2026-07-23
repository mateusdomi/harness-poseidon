using System.Globalization;
using Harness.Host.Workers;
using Harness.Modules.Governance.Context;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Workers;

/// <summary>
/// PLAT-02: prova de fiação end-to-end em SQLite in-process. Sobre um histórico longo e sintético,
/// o <see cref="ChiefContextComposer"/> aplica a <see cref="IContextStrategy"/> real, mantém a janela
/// do Chief dentro do orçamento e persiste as notas externalizadas de forma DURÁVEL — sobrevivendo
/// como linhas do store — de modo idempotente entre turnos.
/// </summary>
public sealed class ChiefContextStrategyPersistenceTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-23T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task LongHistoryStaysBoundedAndNotesArePersistedDurably()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"plat02-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "context.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);

                var tenantId = UlidValue.New(Now).ToString();
                var profileId = UlidValue.New(Now.AddMilliseconds(1)).ToString();
                await InsertTenantAsync(dispatcher, tenantId, timeout.Token);
                await InsertProfileAsync(dispatcher, tenantId, profileId, timeout.Token);

                var organizations = new SqliteOrganizationStore(dispatcher);
                var projects = new SqliteProjectStore(dispatcher);
                var conversations = new SqliteConversationStore(dispatcher);
                var noteStore = new SqliteChiefContextNoteStore(dispatcher);

                var organizationId = UlidValue.New(Now.AddMilliseconds(2)).ToString();
                _ = await organizations.CreateAsync(
                    new OrganizationCreateCommand(
                        tenantId, organizationId, "Ctx", "ctx", "personal",
                        new OrganizationBrandRecord(null, null, null, null), Now),
                    timeout.Token);
                var projectId = UlidValue.New(Now.AddMilliseconds(3)).ToString();
                var chiefAgentId = UlidValue.New(Now.AddMilliseconds(4)).ToString();
                _ = await projects.CreateAsync(
                    new ProjectCreateCommand(
                        tenantId,
                        new ProjectRecord(
                            tenantId, projectId, organizationId, "Ctx", "CTX", "Long history",
                            "active", "medium", null, "local", "main",
                            [], new ProjectBrandRecord(null, null, null, null),
                            [profileId], 1, chiefAgentId, "manual", Now, Now, 0),
                        Now.AddMilliseconds(5)),
                    timeout.Token);

                var conversationId = UlidValue.New(Now.AddMilliseconds(6)).ToString();
                _ = await conversations.CreateConversationAsync(
                    new ConversationCreateCommand(
                        new ConversationRecord(
                            tenantId, conversationId, projectId, "Long", "active", profileId, Now, null, 1),
                        Now),
                    timeout.Token);

                // Histórico longo e sintético: a mensagem mais antiga é o mandato fundador (crítico).
                const int messageCount = 40;
                string? firstMessageId = null;
                string? firstContent = null;
                for (var index = 0; index < messageCount; index++)
                {
                    var at = Now.AddSeconds(index);
                    var messageId = UlidValue.New(at).ToString();
                    firstMessageId ??= messageId;
                    var content = index == 0
                        ? "Founding mandate: deliver the platform control plane with typed contracts and proof gates."
                        : $"Working turn number {index} discussing implementation details with additional filler words here.";
                    firstContent ??= content;
                    _ = await conversations.CreateMessageAsync(
                        new MessageCreateCommand(
                            tenantId,
                            new MessageRecord(
                                tenantId, projectId, messageId, conversationId, "user",
                                profileId, null, content, null, at),
                            at),
                        timeout.Token);
                }

                // Orçamento apertado força a compactação; a estratégia é a implementação padrão real.
                var budget = new ContextStrategyBudget(300, 6, 2);
                var options = new ChiefContextStrategyOptions(true, budget, 200);
                var composer = new ChiefContextComposer(
                    conversations, new DefaultContextStrategy(), noteStore, SystemClock.Instance, options);

                var turnId = UlidValue.New(Now.AddMinutes(1)).ToString();
                var composition = await composer.ComposeAsync(
                    tenantId, projectId, conversationId, turnId, timeout.Token);

                // A janela do Chief permanece limitada abaixo do orçamento (compactada).
                Assert.True(composition.Compacted);
                Assert.True(
                    composition.EstimatedTokens <= budget.MaxTokens,
                    $"Estimated tokens {composition.EstimatedTokens} exceeded budget {budget.MaxTokens}.");
                Assert.True(composition.ItemCount < messageCount);
                Assert.Equal(1, composition.PersistedNoteCount);

                // As notas externalizadas sobrevivem como linhas duráveis do store.
                var persisted = await noteStore.ListAsync(tenantId, projectId, conversationId, 100, timeout.Token);
                var note = Assert.Single(persisted);
                Assert.Equal(firstMessageId, note.SourceItemId);
                Assert.Equal(firstContent, note.Content);
                Assert.Equal(conversationId, note.ConversationId);
                Assert.Equal(turnId, note.TurnId);

                // Idempotência entre turnos: recompor não duplica a nota durável.
                var secondTurnId = UlidValue.New(Now.AddMinutes(2)).ToString();
                var again = await composer.ComposeAsync(
                    tenantId, projectId, conversationId, secondTurnId, timeout.Token);
                Assert.Equal(0, again.PersistedNoteCount);
                Assert.Single(await noteStore.ListAsync(tenantId, projectId, conversationId, 100, timeout.Token));
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

    private static async Task InsertTenantAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tenants(id,name,version,created_at) VALUES($id,$name,0,$at);";
            command.Parameters.AddWithValue("$id", tenantId);
            command.Parameters.AddWithValue("$name", "Tenant " + tenantId);
            command.Parameters.AddWithValue("$at", Store(Now));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private static async Task InsertProfileAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, string profileId, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO local_users(id,tenant_id,display_name,version,created_at) VALUES($id,$tenant,$name,0,$at);";
            command.Parameters.AddWithValue("$id", profileId);
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$name", "Operator");
            command.Parameters.AddWithValue("$at", Store(Now));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
