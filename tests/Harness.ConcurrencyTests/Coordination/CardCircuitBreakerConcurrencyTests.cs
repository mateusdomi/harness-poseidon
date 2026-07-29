using System.Globalization;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Sqlite;

namespace Harness.ConcurrencyTests.Coordination;

/// <summary>
/// F12/B4 — o circuito por card sobre a persistência real. O que estes testes protegem é a regra
/// que distingue este circuito do circuito por conta: <b>tempo não reabre</b>. Um card que falhou
/// três vezes seguidas tem defeito de enunciado, e só o replanejamento da Bruna o devolve à fila.
/// </summary>
public sealed class CardCircuitBreakerConcurrencyTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThreeConsecutiveFailuresOpenTheCircuitAndOnlyReplanReopensIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCardCircuitBreakerStore(dispatcher);
            const string card = "card-enunciado-ambiguo";

            var first = await Fail(store, card, Now, timeout.Token);
            Assert.False(first.IsOpen);
            Assert.Equal(1, first.ConsecutiveFailures);

            var second = await Fail(store, card, Now.AddMinutes(10), timeout.Token);
            Assert.False(second.IsOpen);

            var third = await Fail(store, card, Now.AddMinutes(20), timeout.Token);
            Assert.True(third.IsOpen);
            Assert.Equal(Now.AddMinutes(20), third.OpenedAt);

            // Tempo NÃO reabre: uma semana depois o circuito continua aberto.
            var muchLater = await Fail(store, card, Now.AddDays(7), timeout.Token);
            Assert.True(muchLater.IsOpen);
            Assert.Equal(3, muchLater.ConsecutiveFailures);

            // E o card continua fora do despacho.
            var open = await store.ListOpenAsync(Tenant, Project, timeout.Token);
            Assert.Equal([card], open.Select(record => record.TaskId));

            var replanned = await store.ReplanAsync(
                Tenant, Project, card, Now.AddDays(7).AddHours(1),
                "instrucao reescrita com criterio de aceite verificavel", timeout.Token);
            Assert.False(replanned.IsOpen);
            Assert.Equal(0, replanned.ConsecutiveFailures);
            Assert.NotNull(replanned.ReplannedAt);
            Assert.Empty(await store.ListOpenAsync(Tenant, Project, timeout.Token));
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ConcurrentFailuresOfTheSameCardDoNotDoubleCount()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCardCircuitBreakerStore(dispatcher);
            const string card = "card-concorrente";

            // Duas rodadas relatando falha ao mesmo tempo: a transição inteira é uma transação,
            // então a contagem final é exatamente o número de falhas relatadas.
            await Task.WhenAll(
                Enumerable.Range(0, 2).Select(index =>
                    Fail(store, card, Now.AddSeconds(index), timeout.Token)));

            var circuit = await store.GetAsync(Tenant, card, timeout.Token);
            Assert.NotNull(circuit);
            Assert.Equal(2, circuit!.ConsecutiveFailures);
            Assert.False(circuit.IsOpen);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SuccessResetsTheStreakSoAnOldFailureNeverOpensTheCircuitAlone()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCardCircuitBreakerStore(dispatcher);
            const string card = "card-intermitente";

            await Fail(store, card, Now, timeout.Token);
            await Fail(store, card, Now.AddMinutes(1), timeout.Token);
            await store.RecordSuccessAsync(Tenant, Project, card, Now.AddMinutes(2), timeout.Token);
            var afterSuccess = await Fail(store, card, Now.AddMinutes(3), timeout.Token);

            Assert.False(afterSuccess.IsOpen);
            Assert.Equal(1, afterSuccess.ConsecutiveFailures);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    private static Task<CardCircuitRecord> Fail(
        SqliteCardCircuitBreakerStore store, string card, DateTimeOffset at, CancellationToken token) =>
        store.RecordFailureAsync(
            Tenant, Project, card, at,
            CardCircuitBreakerPolicy.ConsecutiveFailureThreshold,
            "agent.run_failed",
            token);

    private static async Task<(string Root, SqliteWriteDispatcher Dispatcher)> CreateDatabaseAsync(
        CancellationToken token)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f12-card-circuit",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(
            Path.Combine(root, "circuit.db"), token);
        await SqliteMigrationRunner.ApplyAsync(dispatcher, token);
        await dispatcher.ExecuteAsync(
            async (connection, inner) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     INSERT INTO tenants (id, name, created_at)
                     VALUES ('{Tenant}', 'Tenant', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO organizations (id, tenant_id, name, created_at)
                     VALUES ('{Organization}', '{Tenant}', 'Organization', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                     VALUES ('{Project}', '{Tenant}', '{Organization}', 'Project', '2026-07-29T12:00:00.0000000+00:00');
                     """;
                await command.ExecuteNonQueryAsync(inner);
                return 0;
            },
            token);
        return (root, dispatcher);
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Artefato de teste em disco não é resultado: falha ao limpar não reprova a suíte.
        }
    }
}
