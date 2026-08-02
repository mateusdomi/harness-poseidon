using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Persistence;

public sealed class SqliteWorkChainStoreTests
{
    [Fact]
    public async Task CreationIsAtomicIdempotentAuditedAndReadable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-work-chain-sqlite",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "work-chain.db"),
                timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(),
                timeout.Token);
            var store = new SqliteWorkChainStore(dispatcher);
            await WorkChainStoreBehavior.AssertAsync(store, timeout.Token);
            await WorkChainStoreBehavior.AssertReviewUnavailableEscalationAsync(
                store, timeout.Token);
            await WorkChainStoreBehavior.AssertUndispatchableEscalationAsync(
                store, timeout.Token);

            // O consumo medido precisa chegar à projeção do quadro. Sem isso, custo, duração e
            // tokens aparecem zerados na interface mesmo com trabalho real executado — e a
            // auditoria não consegue responder quanto custou nem quanto demorou.
            var attempts = await new SqliteWorkBoardStore(dispatcher).ListAttemptsAsync(
                FoundationTransactionBehavior.TenantId,
                WorkChainStoreBehavior.UnreviewableTaskId,
                null,
                10,
                timeout.Token);
            var attempt = Assert.Single(attempts);
            Assert.Equal(12_345, attempt.DurationMs);
            Assert.Equal(900, attempt.TokensInput);
            Assert.Equal(350, attempt.TokensOutput);
            Assert.Equal(1.25m, attempt.CostUsd);

            // Falha TRANSITÓRIA lida pelo MESMO caminho do Chief (ChiefBacklogLoopService lê
            // ListAttemptsAsync e alimenta o circuito do card). O motivo precisa sobreviver à
            // escrita mesmo sem consumir rodada: 'cancelled' SEM motivo é indistinguível de um
            // reinício do Host, e foi assim que nove falhas seguidas no mesmo card não contaram
            // nenhuma. A combinação abaixo é a que `CardCircuitBreakerService.IsFailure` exige.
            var transient = Assert.Single(await new SqliteWorkBoardStore(dispatcher).ListAttemptsAsync(
                FoundationTransactionBehavior.TenantId,
                WorkChainStoreBehavior.TransientFailureTaskId,
                null,
                10,
                timeout.Token));
            Assert.Equal("cancelled", transient.State);
            Assert.Equal(WorkChainStoreBehavior.TransientFailureReason, transient.FailureReason);

            // Um card com MAIS tentativas que o limite não pode esconder as recentes.
            //
            // Com `ORDER BY a.id LIMIT` (ULID cresce com o tempo), a página devolvia as mais
            // ANTIGAS. Medido em produção: dois cards com 101 tentativas e limite 100 — a
            // tentativa EM EXECUÇÃO era a 101ª e sumia. A colheita não achava o run vivo, a
            // reconciliação não fechava a órfã, o circuito contava falhas de um passado
            // congelado e o card ficava preso por dias sem emitir um único sinal.
            var board = new SqliteWorkBoardStore(dispatcher);
            var pageOfTwo = await board.ListAttemptsAsync(
                FoundationTransactionBehavior.TenantId,
                WorkChainStoreBehavior.CrowdedTaskId,
                null,
                2,
                timeout.Token);

            Assert.Equal(2, pageOfTwo.Count);
            // As duas ÚLTIMAS, em ordem cronológica — o circuito depende dessa ordem.
            Assert.Equal(
                WorkChainStoreBehavior.CrowdedNewestAttemptId,
                pageOfTwo[^1].Id);
            Assert.DoesNotContain(
                pageOfTwo,
                attempt => attempt.Id == WorkChainStoreBehavior.CrowdedOldestAttemptId);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }
}
