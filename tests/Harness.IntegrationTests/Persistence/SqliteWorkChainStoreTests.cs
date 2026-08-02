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
