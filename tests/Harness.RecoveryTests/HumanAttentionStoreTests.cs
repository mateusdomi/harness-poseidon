using Harness.Persistence.Abstractions.Attention;
using Harness.Persistence.Sqlite;

namespace Harness.RecoveryTests;

/// <summary>
/// Human Attention Loop — a store durável sobre banco real: criação idempotente por correlação
/// (o loop re-observa o mesmo card escalado a cada ciclo sem duplicar nem reiniciar lembretes),
/// transições de estado, e ISOLAMENTO por projeto (um ASK do Prisma jamais aparece como
/// Indicadores — Parte 18 da missão).
/// </summary>
public sealed class HumanAttentionStoreTests : IAsyncLifetime
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5T01";
    private const string Org = "01ARZ3NDEKTSV4RRFFQ69G5T02";
    private const string ProjectA = "01ARZ3NDEKTSV4RRFFQ69G5TA1";
    private const string ProjectB = "01ARZ3NDEKTSV4RRFFQ69G5TB1";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 18, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-attention-{Guid.NewGuid():N}");

    private SqliteWriteDispatcher _dispatcher = null!;
    private SqliteHumanAttentionStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(_root, "attention.db"));
        await SqliteMigrationRunner.ApplyAsync(_dispatcher, CancellationToken.None);
        await _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                $"""
                INSERT INTO tenants (id,name,created_at) VALUES ('{Tenant}','t','2026-08-05T00:00:00Z');
                INSERT INTO organizations (id,tenant_id,name,created_at)
                    VALUES ('{Org}','{Tenant}','TrensRJ','2026-08-05T00:00:00Z');
                INSERT INTO projects (id,tenant_id,organization_id,name,created_at)
                    VALUES ('{ProjectA}','{Tenant}','{Org}','Prisma','2026-08-05T00:00:00Z');
                INSERT INTO projects (id,tenant_id,organization_id,name,created_at)
                    VALUES ('{ProjectB}','{Tenant}','{Org}','Indicadores','2026-08-05T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);
        _store = new SqliteHumanAttentionStore(_dispatcher);
    }

    public async Task DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza de temp não decide teste.
        }
    }

    private static HumanAttentionCreateCommand Command(
        string id, string project, string correlation) =>
        new(
            Tenant, id, project,
            "Há duas regras contraditórias sobre quem aprova a reversão de carga; qual vale?",
            "Divergência entre a seção de perfis e a de histórico de cargas.",
            "high", "import-reversal", "requirements-intake", correlation, Now);

    [Fact]
    public async Task ACriacaoEIdempotentePorCorrelacaoENaoReiniciaLembretes()
    {
        var first = await _store.CreateAsync(
            Command("01ARZ3NDEKTSV4RRFFQ69G5T10", ProjectA, "card-escalated:c1"));
        await _store.RecordEscalationAsync(
            Tenant, first.Id, "notified", 2, Now.AddMinutes(60), "telegram_sent");

        // O loop re-observa o MESMO card no ciclo seguinte: nada muda.
        var replay = await _store.CreateAsync(
            Command("01ARZ3NDEKTSV4RRFFQ69G5T11", ProjectA, "card-escalated:c1"));

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(2, replay.ReminderCount);
        Assert.Equal("notified", replay.Status);
    }

    [Fact]
    public async Task RespostaFechaOPedidoERegistraOTempoDeResposta()
    {
        var request = await _store.CreateAsync(
            Command("01ARZ3NDEKTSV4RRFFQ69G5T20", ProjectA, "ask:aprovacao"));
        await _store.AcknowledgeAsync(Tenant, request.Id, Now.AddMinutes(9));
        await _store.AnswerAsync(
            Tenant, request.Id, "Vale a regra da seção de perfis; reversão exige perfil Gestor.",
            Now.AddMinutes(11));

        var stored = await _store.GetByCorrelationAsync(Tenant, "ask:aprovacao");
        Assert.NotNull(stored);
        Assert.Equal("answered", stored.Status);
        Assert.NotNull(stored.Answer);
        // A métrica da prova empresarial: mediana de tempo de resposta deriva DESTES campos.
        Assert.Equal(Now.AddMinutes(9), stored.AcknowledgedAt);
        Assert.Equal(Now.AddMinutes(11), stored.AnsweredAt);
        // Respondido some da fila aberta — nenhum lembrete posterior.
        Assert.Empty(await _store.ListOpenAsync(Tenant, ProjectA));
    }

    /// <summary>Parte 18 — um ASK do Prisma nunca aparece como Indicadores, e vice-versa.</summary>
    [Fact]
    public async Task AtencaoEIsoladaPorProjeto()
    {
        await _store.CreateAsync(Command("01ARZ3NDEKTSV4RRFFQ69G5T30", ProjectA, "ask:a"));
        await _store.CreateAsync(Command("01ARZ3NDEKTSV4RRFFQ69G5T31", ProjectB, "ask:b"));

        var forA = await _store.ListOpenAsync(Tenant, ProjectA);
        var forB = await _store.ListOpenAsync(Tenant, ProjectB);

        Assert.Equal(ProjectA, Assert.Single(forA).ProjectId);
        Assert.Equal(ProjectB, Assert.Single(forB).ProjectId);
        // E o bloqueio de A não muda NADA em B: o pedido de A aberto, B continua sem pedidos
        // além do seu — despachabilidade de B é assunto do readiness de B.
        Assert.Equal(2, (await _store.ListOpenAsync(Tenant)).Count);
    }
}
