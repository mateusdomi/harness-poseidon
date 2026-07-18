using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Persistence;

public sealed class SqliteWorkflowStoreTests
{
    [Fact]
    public async Task PublishedDefinitionIsAtomicIdempotentAuditedAndReadable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-workflow-store-sqlite",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "workflow.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            await WorkflowStoreBehavior.AssertAsync(new SqliteWorkflowStore(dispatcher), timeout.Token);
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
