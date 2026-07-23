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
                var profile = (await new SqliteLocalProfileStore(dispatcher)
                    .ListAsync(timeout.Token))[0];
                await CatalogStoreBehavior.AssertAsync(
                    new SqliteAgentCatalogStore(dispatcher),
                    new SqliteToolCatalogStore(dispatcher),
                    new SqliteProviderCatalogStore(dispatcher),
                    new SqliteTeamSpecialtyCatalogStore(dispatcher),
                    profile.TenantId,
                    timeout.Token);
                await GovernanceRuntimeStoreBehavior.AssertAsync(
                    new SqliteGovernanceRuntimeStore(dispatcher),
                    profile.TenantId,
                    timeout.Token);
                var organizations = new SqliteOrganizationStore(dispatcher);
                var projects = new SqliteProjectStore(dispatcher);
                var now = DateTimeOffset.UtcNow;
                var organizationId = Harness.SharedKernel.Identifiers.UlidValue.New(now).ToString();
                _ = await organizations.CreateAsync(
                    new Harness.Persistence.Abstractions.Organizations.OrganizationCreateCommand(
                        profile.TenantId, organizationId, "Chat", "chat", "personal",
                        new Harness.Persistence.Abstractions.Organizations.OrganizationBrandRecord(null, null, null, null),
                        now),
                    timeout.Token);
                var projectId = Harness.SharedKernel.Identifiers.UlidValue.New(now.AddMilliseconds(1)).ToString();
                var chiefAgentId = Harness.SharedKernel.Identifiers.UlidValue.New(now.AddMilliseconds(2)).ToString();
                _ = await projects.CreateAsync(
                    new Harness.Persistence.Abstractions.Projects.ProjectCreateCommand(
                        profile.TenantId,
                        new Harness.Persistence.Abstractions.Projects.ProjectRecord(
                            profile.TenantId, projectId, organizationId, "Chat", "CHAT", "Paridade",
                            "active", "medium", null, "local", "main",
                            [], new Harness.Persistence.Abstractions.Projects.ProjectBrandRecord(null, null, null, null),
                            [profile.Id], 1, chiefAgentId, "manual", now, now, 0),
                        now.AddMilliseconds(3)),
                    timeout.Token);
                await LearningCandidateStoreBehavior.AssertAsync(
                    new SqliteLearningCandidateStore(dispatcher), profile.TenantId, organizationId,
                    projectId, profile.Id, timeout.Token);
                var conversationStore = new SqliteConversationStore(dispatcher);
                await ConversationChiefStoreBehavior.AssertAsync(
                    conversationStore,
                    conversationStore,
                    profile.TenantId,
                    projectId,
                    profile.Id,
                    chiefAgentId,
                    tenant => dispatcher.ExecuteAsync(async (connection, ct) =>
                    {
                        await using var count = connection.CreateCommand();
                        count.CommandText = "SELECT COUNT(*) FROM demands WHERE tenant_id=$tenant;";
                        count.Parameters.AddWithValue("$tenant", tenant);
                        return Convert.ToInt32(
                            await count.ExecuteScalarAsync(ct),
                            System.Globalization.CultureInfo.InvariantCulture);
                    }, timeout.Token),
                    timeout.Token);
                await BoardWorkflowProjectionBehavior.AssertAsync(
                    new SqliteWorkBoardStore(dispatcher),
                    new SqliteWorkflowStore(dispatcher),
                    new SqliteWorkflowCatalogStore(dispatcher),
                    profile.TenantId,
                    projectId,
                    profile.Id,
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
