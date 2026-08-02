using System.Globalization;
using Harness.Host.Agents;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Coordination;

/// <summary>
/// F12/B4 — a sincronização do circuito a partir do HISTÓRICO de tentativas. Derivar em vez de
/// incrementar em cada ponto de falha é o que torna a contagem idempotente: reprocessar o mesmo
/// histórico precisa dar exatamente o mesmo estado, inclusive no número gravado.
/// </summary>
public sealed class CardCircuitBreakerSynchronizationTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DerivedCountMatchesWhatIsPersistedAndResyncIsANoOp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCardCircuitBreakerStore(dispatcher);
            var service = new CardCircuitBreakerService(store);
            CardAttemptOutcome[] history =
            [
                Outcome("failed", Now),
                Outcome("failed", Now.AddMinutes(10)),
                Outcome("failed", Now.AddMinutes(20))
            ];

            var snapshot = await service.SynchronizeAsync(
                Tenant, Project, "card-1", history, timeout.Token);

            Assert.Equal(CardCircuitState.Open, snapshot.State);
            var persisted = await store.GetAsync(Tenant, "card-1", timeout.Token);
            Assert.NotNull(persisted);
            Assert.True(persisted!.IsOpen);

            // O número gravado é o número derivado — não um atalho que registraria "1".
            Assert.Equal(3, persisted.ConsecutiveFailures);
            var openCards = await service.ListOpenCardsAsync(Tenant, Project, timeout.Token);
            Assert.Contains("card-1", openCards);

            // Reprocessar o mesmo histórico não muda nada: a operação é idempotente.
            var again = await service.SynchronizeAsync(
                Tenant, Project, "card-1", history, timeout.Token);
            var afterResync = await store.GetAsync(Tenant, "card-1", timeout.Token);
            Assert.Equal(CardCircuitState.Open, again.State);
            Assert.Equal(3, afterResync!.ConsecutiveFailures);
            Assert.Equal(persisted.OpenedAt, afterResync.OpenedAt);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task FailuresBeforeAReplanDoNotCountAgainstTheRewrittenCard()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCardCircuitBreakerStore(dispatcher);
            var service = new CardCircuitBreakerService(store);
            CardAttemptOutcome[] history =
            [
                Outcome("failed", Now),
                Outcome("failed", Now.AddMinutes(10)),
                Outcome("failed", Now.AddMinutes(20))
            ];

            await service.SynchronizeAsync(Tenant, Project, "card-1", history, timeout.Token);
            await service.ReplanAsync(
                Tenant, Project, "card-1", Now.AddMinutes(30), "criterio de aceite reescrito",
                timeout.Token);

            // O mesmo histórico antigo já não abre nada: aquelas falhas são de outro enunciado.
            var afterReplan = await service.SynchronizeAsync(
                Tenant, Project, "card-1", history, timeout.Token);
            Assert.Equal(CardCircuitState.Closed, afterReplan.State);
            Assert.Equal(0, afterReplan.ConsecutiveFailures);
            Assert.Empty(await service.ListOpenCardsAsync(Tenant, Project, timeout.Token));

            // Mas falhas POSTERIORES ao replanejamento voltam a contar do zero.
            var afterNewFailures = await service.SynchronizeAsync(
                Tenant, Project, "card-1",
                [.. history, Outcome("failed", Now.AddMinutes(40)), Outcome("failed", Now.AddMinutes(50))],
                timeout.Token);
            Assert.Equal(CardCircuitState.Closed, afterNewFailures.State);
            Assert.Equal(2, afterNewFailures.ConsecutiveFailures);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SuccessAfterFailuresClearsThePersistedStreak()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCardCircuitBreakerStore(dispatcher);
            var service = new CardCircuitBreakerService(store);

            await service.SynchronizeAsync(
                Tenant, Project, "card-1",
                [Outcome("failed", Now), Outcome("failed", Now.AddMinutes(5))],
                timeout.Token);
            Assert.Equal(2, (await store.GetAsync(Tenant, "card-1", timeout.Token))!.ConsecutiveFailures);

            var snapshot = await service.SynchronizeAsync(
                Tenant, Project, "card-1",
                [Outcome("failed", Now), Outcome("failed", Now.AddMinutes(5)), Outcome("completed", Now.AddMinutes(9))],
                timeout.Token);

            Assert.Equal(0, snapshot.ConsecutiveFailures);
            Assert.Equal(0, (await store.GetAsync(Tenant, "card-1", timeout.Token))!.ConsecutiveFailures);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task FalhaDeExecucaoContaMesmoChegandoComoCancelamento()
    {
        // Observado em execução real: nove tentativas seguidas no mesmo card, todas mortas por
        // falha do executor, e o circuito parado em UMA. A cadeia projeta uma tentativa expirada
        // como `cancelled`, e o circuito só contava `failed`/`rejected` — então a falha de
        // execução, que é justamente o sinal de que o card não anda, era invisível.
        //
        // O MOTIVO é o que separa falha de execução de um cancelamento de infraestrutura: só a
        // primeira grava um. Um reinício do Host continua não punindo o card, porque o circuito
        // só reabre por replanejamento e um falso positivo aqui PARARIA trabalho saudável.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCardCircuitBreakerStore(dispatcher);
            var service = new CardCircuitBreakerService(store);

            var comMotivo = await service.SynchronizeAsync(
                Tenant, Project, "card-falho",
                [
                    Outcome("cancelled", Now, "executor.exit_code_1"),
                    Outcome("cancelled", Now.AddMinutes(2), "executor.exit_code_1"),
                    Outcome("cancelled", Now.AddMinutes(4), "executor.exit_code_1"),
                ],
                timeout.Token);
            Assert.Equal(CardCircuitState.Open, comMotivo.State);
            Assert.Equal(3, comMotivo.ConsecutiveFailures);

            var semMotivo = await service.SynchronizeAsync(
                Tenant, Project, "card-reiniciado",
                [
                    Outcome("cancelled", Now),
                    Outcome("cancelled", Now.AddMinutes(2)),
                    Outcome("cancelled", Now.AddMinutes(4)),
                ],
                timeout.Token);
            Assert.Equal(CardCircuitState.Closed, semMotivo.State);
            Assert.Equal(0, semMotivo.ConsecutiveFailures);

            // Reinício do Host COM motivo gravado. Desde que a expiração de lease passou a
            // registrar o motivo, um restart deixou de ser cancelamento anônimo — bom para a
            // auditoria, e uma armadilha para o circuito: derrubar o Host três vezes durante
            // tentativas de um card saudável abriria o circuito dele, e só o replanejamento da
            // Bruna reabre. O motivo descreve a INFRAESTRUTURA, não o card, e não pode contar.
            var reiniciado = await service.SynchronizeAsync(
                Tenant, Project, "card-host-caiu",
                [
                    Outcome("cancelled", Now, "attempt.interrupted_by_host_shutdown"),
                    Outcome("cancelled", Now.AddMinutes(2), "attempt.orphaned_by_host_restart"),
                    Outcome("cancelled", Now.AddMinutes(4), "attempt.interrupted_by_host_shutdown"),
                ],
                timeout.Token);
            Assert.Equal(CardCircuitState.Closed, reiniciado.State);
            Assert.Equal(0, reiniciado.ConsecutiveFailures);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            Cleanup(root);
        }
    }

    private static CardAttemptOutcome Outcome(
        string state, DateTimeOffset occurredAt, string? failureReason = null) =>
        new(state, failureReason, occurredAt);

    private static async Task<(string Root, SqliteWriteDispatcher Dispatcher)> CreateDatabaseAsync(
        CancellationToken token)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f12-circuit-sync",
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
            // Artefato em disco não é resultado: falha ao limpar não reprova a suíte.
        }
    }
}
