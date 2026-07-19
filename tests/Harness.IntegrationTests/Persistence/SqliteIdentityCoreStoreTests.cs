using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Persistence;

public sealed class SqliteIdentityCoreStoreTests
{
    [Fact]
    public async Task IdentityCoreBehaviorHoldsOnSqlite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"identity-core-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "identity.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                await IdentityCoreStoreBehavior.AssertAsync(
                    new SqliteLocalProfileStore(dispatcher),
                    new SqliteOrganizationStore(dispatcher),
                    new SqliteProjectStore(dispatcher),
                    new SqliteAuditEventStore(dispatcher),
                    timeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
