using System.Globalization;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Sqlite;

namespace Harness.RecoveryTests;

/// <summary>
/// O vínculo de canal precisa sobreviver ao reinício do Poseidon.
///
/// Pedido pelo proprietário em 03/08/2026, com um motivo concreto: ele já tinha vinculado o
/// Telegram "outras vezes" e, depois de reiniciar, o bot voltava a responder "esta conversa
/// ainda não está vinculada". Um vínculo que se perde é pior que um que nunca existiu — o dono
/// refaz a mesma configuração indefinidamente, e só descobre que ela sumiu quando fala com o
/// produto e ninguém responde.
///
/// Estas provas fixam as duas metades: o vínculo sobrevive a um processo novo, e a criação é
/// idempotente por identidade — reinstalar o mesmo vínculo não cria um segundo nem troca o
/// destino do primeiro.
/// </summary>
public sealed class ChannelLinkRecoveryTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string Profile = "01ARZ3NDEKTSV4RRFFQ69G5FAY";
    private const string Conversation = "01ARZ3NDEKTSV4RRFFQ69G5FAZ";
    private const string ChatId = "5774120296";

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ALinkedChannelIsStillLinkedAfterTheProductRestarts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = CreateRoot("channel-link-restart");
        var databasePath = Path.Combine(root, "channels.db");
        string linkId;

        try
        {
            await using (var dispatcher = await OpenAsync(databasePath, timeout.Token, seed: true))
            {
                var store = new SqliteChannelLinkStore(dispatcher);
                var created = await store.GetOrCreateAsync(Command(), timeout.Token);
                linkId = created.Id;
                Assert.Equal(ChatId, created.ExternalIdentity);
            }

            // Reinício do produto: outro dispatcher, outra instância de store, o mesmo arquivo.
            await using (var dispatcher = await OpenAsync(databasePath, timeout.Token))
            {
                var store = new SqliteChannelLinkStore(dispatcher);

                var recovered = await store.GetAsync(Tenant, linkId, timeout.Token);
                Assert.NotNull(recovered);
                Assert.Equal(ChatId, recovered!.ExternalIdentity);

                // É por ESTA listagem que o poller decide se conhece quem está falando: um
                // vínculo que não aparece aqui é, na prática, um vínculo que não existe.
                var listed = await store.ListAsync(Tenant, timeout.Token);
                Assert.Contains(listed, link =>
                    link.Kind == "telegram" &&
                    string.Equals(link.ExternalIdentity, ChatId, StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Vincular de novo o mesmo chat não pode duplicar nem trocar o destino. Sem isto, cada
    /// tentativa de "reconfigurar" deixaria um vínculo órfão para trás e o roteamento passaria
    /// a depender de qual deles a listagem devolvesse primeiro.
    /// </summary>
    [Fact]
    public async Task LinkingTheSameChatAgainReturnsTheSameLinkInsteadOfADuplicate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = CreateRoot("channel-link-idempotent");
        var databasePath = Path.Combine(root, "channels.db");

        try
        {
            await using var dispatcher = await OpenAsync(databasePath, timeout.Token, seed: true);
            var store = new SqliteChannelLinkStore(dispatcher);

            var first = await store.GetOrCreateAsync(Command(), timeout.Token);
            var second = await store.GetOrCreateAsync(
                Command(id: "01ARZ3NDEKTSV4RRFFQ69G5FB9"), timeout.Token);

            Assert.Equal(first.Id, second.Id);
            Assert.Single(
                await store.ListAsync(Tenant, timeout.Token),
                link => string.Equals(link.ExternalIdentity, ChatId, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ChannelLinkCreateCommand Command(string id = "01ARZ3NDEKTSV4RRFFQ69G5FB1") =>
        new(Tenant, id, "telegram", ChatId, Profile, Project, Conversation, Now);

    private static string CreateRoot(string name)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            name,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<SqliteWriteDispatcher> OpenAsync(
        string databasePath, CancellationToken token, bool seed = false)
    {
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, token);
        await SqliteMigrationRunner.ApplyAsync(dispatcher, token);
        if (!seed)
        {
            return dispatcher;
        }

        const string At = "2026-08-03T10:00:00.0000000+00:00";
        await dispatcher.ExecuteAsync(
            async (connection, inner) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     INSERT INTO tenants (id, name, created_at)
                     VALUES ('{Tenant}', 'Tenant', '{At}');
                     INSERT INTO organizations (id, tenant_id, name, created_at)
                     VALUES ('{Organization}', '{Tenant}', 'Organization', '{At}');
                     INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                     VALUES ('{Project}', '{Tenant}', '{Organization}', 'Project', '{At}');
                     INSERT INTO local_users (id, tenant_id, display_name, version, created_at, locale, role)
                     VALUES ('{Profile}', '{Tenant}', 'Dono', 1, '{At}', 'pt-BR', 'admin');
                     INSERT INTO conversations
                        (id, tenant_id, project_id, title, state, created_by_profile_id, created_at, version)
                     VALUES ('{Conversation}', '{Tenant}', '{Project}', 'Conversa', 'active', '{Profile}', '{At}', 1);
                     """;
                await command.ExecuteNonQueryAsync(inner);
                return 0;
            },
            token);

        return dispatcher;
    }
}
