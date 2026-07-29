using System.Globalization;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Conversations;

/// <summary>
/// F2/D16 — o dono abria uma conversa, navegava e voltava ao Chat na tela inicial, perdendo o fio
/// do que estava combinando com a Bruna. A restauração é por PERFIL e PROJETO: guardar só por
/// perfil abriria a conversa errada logo depois de trocar de projeto, trocando um bug por outro.
/// </summary>
public sealed class ProfileActiveConversationTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string ProjectA = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string ProjectB = "01ARZ3NDEKTSV4RRFFQ69G5FAY";
    private const string Profile = "01ARZ3NDEKTSV4RRFFQ69G5U01";
    private const string Conversation1 = "01ARZ3NDEKTSV4RRFFQ69G5C01";
    private const string Conversation2 = "01ARZ3NDEKTSV4RRFFQ69G5C02";
    private const string ConversationB = "01ARZ3NDEKTSV4RRFFQ69G5C03";
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheConversationTheOwnerWasInIsRestoredAfterNavigatingAway()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateAsync(timeout.Token);
        try
        {
            var store = new SqliteProfileActiveConversationStore(dispatcher);
            await store.RememberAsync(
                new ProfileActiveConversationRecord(Tenant, Profile, ProjectA, Conversation1, Now),
                timeout.Token);

            // Navegou para outra tela e voltou ao Chat.
            var restored = await store.RecallAsync(Tenant, Profile, ProjectA, timeout.Token);

            Assert.NotNull(restored);
            Assert.Equal(Conversation1, restored!.ConversationId);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    /// <summary>Trocar de projeto NÃO pode restaurar a conversa do projeto anterior.</summary>
    [Fact]
    public async Task EachProjectKeepsItsOwnConversation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateAsync(timeout.Token);
        try
        {
            var store = new SqliteProfileActiveConversationStore(dispatcher);
            await store.RememberAsync(
                new ProfileActiveConversationRecord(Tenant, Profile, ProjectA, Conversation1, Now),
                timeout.Token);
            await store.RememberAsync(
                new ProfileActiveConversationRecord(
                    Tenant, Profile, ProjectB, ConversationB, Now.AddMinutes(1)),
                timeout.Token);

            Assert.Equal(
                Conversation1,
                (await store.RecallAsync(Tenant, Profile, ProjectA, timeout.Token))!.ConversationId);
            Assert.Equal(
                ConversationB,
                (await store.RecallAsync(Tenant, Profile, ProjectB, timeout.Token))!.ConversationId);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task OpeningAnotherConversationReplacesInsteadOfAccumulating()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateAsync(timeout.Token);
        try
        {
            var store = new SqliteProfileActiveConversationStore(dispatcher);
            await store.RememberAsync(
                new ProfileActiveConversationRecord(Tenant, Profile, ProjectA, Conversation1, Now),
                timeout.Token);
            await store.RememberAsync(
                new ProfileActiveConversationRecord(
                    Tenant, Profile, ProjectA, Conversation2, Now.AddMinutes(5)),
                timeout.Token);

            var restored = await store.RecallAsync(Tenant, Profile, ProjectA, timeout.Token);
            Assert.Equal(Conversation2, restored!.ConversationId);
            Assert.Equal(Now.AddMinutes(5), restored.UpdatedAt);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task AProfileThatNeverOpenedAConversationHasNothingToRestore()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateAsync(timeout.Token);
        try
        {
            var store = new SqliteProfileActiveConversationStore(dispatcher);
            Assert.Null(await store.RecallAsync(Tenant, "01ARZ3NDEKTSV4RRFFQ69G5U02", ProjectA, timeout.Token));
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ForgettingLeavesNothingToRestore()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateAsync(timeout.Token);
        try
        {
            var store = new SqliteProfileActiveConversationStore(dispatcher);
            await store.RememberAsync(
                new ProfileActiveConversationRecord(Tenant, Profile, ProjectA, Conversation1, Now),
                timeout.Token);
            await store.ForgetAsync(Tenant, Profile, ProjectA, timeout.Token);

            // Restaurar conversa apagada abriria o Chat numa tela vazia — o bug de volta.
            Assert.Null(await store.RecallAsync(Tenant, Profile, ProjectA, timeout.Token));
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task TheChoiceSurvivesARestartBecauseItLivesOnTheServer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Root();
        var databasePath = Path.Combine(root, "conversas.db");
        try
        {
            await using (var dispatcher = await OpenAsync(databasePath, seed: true, timeout.Token))
            {
                await new SqliteProfileActiveConversationStore(dispatcher).RememberAsync(
                    new ProfileActiveConversationRecord(Tenant, Profile, ProjectA, Conversation1, Now),
                    timeout.Token);
            }

            await using (var dispatcher = await OpenAsync(databasePath, seed: false, timeout.Token))
            {
                var restored = await new SqliteProfileActiveConversationStore(dispatcher)
                    .RecallAsync(Tenant, Profile, ProjectA, timeout.Token);
                Assert.Equal(Conversation1, restored!.ConversationId);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string Root()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f2-active-conversation",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(string Root, SqliteWriteDispatcher Dispatcher)> CreateAsync(
        CancellationToken token)
    {
        var root = Root();
        var dispatcher = await OpenAsync(Path.Combine(root, "conversas.db"), seed: true, token);
        return (root, dispatcher);
    }

    private static async Task<SqliteWriteDispatcher> OpenAsync(
        string databasePath, bool seed, CancellationToken token)
    {
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, token);
        await SqliteMigrationRunner.ApplyAsync(dispatcher, token);
        if (!seed)
        {
            return dispatcher;
        }

        await dispatcher.ExecuteAsync(
            async (connection, inner) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     INSERT INTO tenants (id, name, created_at)
                     VALUES ('{Tenant}', 'Tenant', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO organizations (id, tenant_id, name, created_at)
                     VALUES ('{Organization}', '{Tenant}', 'Org', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                     VALUES ('{ProjectA}', '{Tenant}', '{Organization}', 'A', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                     VALUES ('{ProjectB}', '{Tenant}', '{Organization}', 'B', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO local_users (id, tenant_id, display_name, created_at)
                     VALUES ('{Profile}', '{Tenant}', 'Dono', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO conversations (id, tenant_id, project_id, title, state, created_by_profile_id, created_at, version)
                     VALUES ('{Conversation1}', '{Tenant}', '{ProjectA}', 'Conversa 1', 'active', '{Profile}', '2026-07-29T12:00:00.0000000+00:00', 1);
                     INSERT INTO conversations (id, tenant_id, project_id, title, state, created_by_profile_id, created_at, version)
                     VALUES ('{Conversation2}', '{Tenant}', '{ProjectA}', 'Conversa 2', 'active', '{Profile}', '2026-07-29T12:00:00.0000000+00:00', 1);
                     INSERT INTO conversations (id, tenant_id, project_id, title, state, created_by_profile_id, created_at, version)
                     VALUES ('{ConversationB}', '{Tenant}', '{ProjectB}', 'Conversa B', 'active', '{Profile}', '2026-07-29T12:00:00.0000000+00:00', 1);
                     """;
                await command.ExecuteNonQueryAsync(inner);
                return 0;
            },
            token);
        return dispatcher;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Artefato em disco não é resultado.
        }
    }
}
