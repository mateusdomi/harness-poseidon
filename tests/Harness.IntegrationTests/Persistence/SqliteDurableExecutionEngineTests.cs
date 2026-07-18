using Harness.Persistence.Abstractions.Foundation;
using Harness.IntegrationTests.Workers;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Persistence;

public sealed class SqliteDurableExecutionEngineTests
{
    [Fact]
    public async Task LifecycleIsIdempotentFencedRetryableAndRecoverable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-durable-sqlite",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "durable.db"),
                timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await SeedFoundationAsync(dispatcher, timeout.Token);
            var engine = new SqliteDurableExecutionEngine(dispatcher);
            await DurableExecutionWatchdogBehavior.AssertAsync(engine, timeout.Token);
            await DurableExecutionEngineBehavior.AssertAsync(engine, timeout.Token);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static async Task SeedFoundationAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var command = new ProjectProvisionCommand(
            DurableExecutionEngineBehavior.TenantId,
            "Tenant",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "Organization",
            DurableExecutionEngineBehavior.ProjectId,
            "Project",
            "01ARZ3NDEKTSV4RRFFQ69G5FAY",
            "Local User",
            "durable:foundation",
            new string('D', 64),
            "01ARZ3NDEKTSV4RRFFQ69G5FAZ",
            "01ARZ3NDEKTSV4RRFFQ69G5FB0",
            "project.created",
            "{\"projectId\":\"01ARZ3NDEKTSV4RRFFQ69G5FAX\"}",
            new DateTimeOffset(2026, 7, 18, 14, 19, 0, TimeSpan.Zero));
        await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(command, cancellationToken);
    }
}
