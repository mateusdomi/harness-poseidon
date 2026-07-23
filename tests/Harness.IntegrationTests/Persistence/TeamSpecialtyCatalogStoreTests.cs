using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// CAT-04: catálogos reais e tenant-scoped de team/specialty em SQLite in-process. Cobre CRUD,
/// isolamento por tenant, unicidade de nome por tenant, especialidade que pertence a uma equipe,
/// guarda referencial no delete (conflito tipado) e a validação referencial das definições de
/// agente contra o catálogo.
/// </summary>
public sealed class TeamSpecialtyCatalogStoreTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-23T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TeamSpecialtyCatalogIsTenantScopedCrudWithReferentialGuards()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var root = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", $"cat04-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "catalog.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                var catalog = new SqliteTeamSpecialtyCatalogStore(dispatcher);
                var agents = new SqliteAgentCatalogStore(dispatcher);
                var tenantA = UlidValue.New(Now).ToString();
                var tenantB = UlidValue.New(Now.AddSeconds(1)).ToString();
                // Definições de agente escrevem no audit_ledger, cujo tenant_id referencia tenants(id).
                await InsertTenantAsync(dispatcher, tenantA, timeout.Token);
                await InsertTenantAsync(dispatcher, tenantB, timeout.Token);

                // Team CRUD happy path.
                var team = await catalog.CreateTeamAsync(new(tenantA, tenantA, NewId(), null, "Engineering", "Builds things.", Now), timeout.Token);
                Assert.Equal("engineering", team.Key);
                Assert.Equal("Engineering", team.Name);
                Assert.Equal(team.Id, (await catalog.GetTeamAsync(tenantA, team.Id, timeout.Token))!.Id);
                var renamed = await catalog.UpdateTeamAsync(new(tenantA, tenantA, team.Id, null, "Platform Engineering", null, Now), timeout.Token);
                Assert.Equal("Platform Engineering", renamed.Name);
                Assert.Null(renamed.Description);
                Assert.Equal("Platform Engineering", Assert.Single(await catalog.ListTeamsAsync(tenantA, null, 50, timeout.Token)).Name);

                // Unique name per tenant (case-insensitive), but a different tenant is free to reuse it.
                await Assert.ThrowsAsync<TeamSpecialtyCatalogConflictException>(() =>
                    catalog.CreateTeamAsync(new(tenantA, tenantA, NewId(), null, "platform engineering", null, Now), timeout.Token));
                _ = await catalog.CreateTeamAsync(new(tenantB, tenantB, NewId(), null, "Platform Engineering", null, Now), timeout.Token);

                // Tenant isolation: tenant B cannot see or mutate tenant A's team.
                Assert.Null(await catalog.GetTeamAsync(tenantB, team.Id, timeout.Token));
                Assert.DoesNotContain(await catalog.ListTeamsAsync(tenantB, null, 50, timeout.Token), value => value.Id == team.Id);
                await Assert.ThrowsAsync<TeamSpecialtyCatalogNotFoundException>(() =>
                    catalog.DeleteTeamAsync(new(tenantB, tenantB, team.Id, Now), timeout.Token));

                // Specialty belongs (optionally) to a team; a bogus team reference is rejected.
                await Assert.ThrowsAsync<TeamSpecialtyCatalogValidationException>(() =>
                    catalog.CreateSpecialtyAsync(new(tenantA, tenantA, NewId(), null, "Backend", null, NewId(), Now), timeout.Token));
                var specialty = await catalog.CreateSpecialtyAsync(new(tenantA, tenantA, NewId(), null, "Backend", null, team.Id, Now), timeout.Token);
                Assert.Equal(team.Id, specialty.TeamId);
                Assert.Equal("backend", specialty.Key);
                Assert.Equal(specialty.Id, Assert.Single(await catalog.ListSpecialtiesAsync(tenantA, team.Id, null, 50, timeout.Token)).Id);
                await Assert.ThrowsAsync<TeamSpecialtyCatalogConflictException>(() =>
                    catalog.CreateSpecialtyAsync(new(tenantA, tenantA, NewId(), null, "backend", null, null, Now), timeout.Token));

                // A team referenced by a specialty cannot be deleted (typed conflict).
                await Assert.ThrowsAsync<TeamSpecialtyCatalogConflictException>(() =>
                    catalog.DeleteTeamAsync(new(tenantA, tenantA, team.Id, Now), timeout.Token));

                // An agent definition may reference existing catalog entries by name; an unknown one is rejected.
                var content = new AgentDefinitionContent(
                    "backend-engineer", "Backend Engineer", "specialist", "Backend", "Builds backend.",
                    null, [], [], null, null, [], [], [], null, [], [], null, null, [],
                    "Platform Engineering", null, null);
                var definition = await agents.CreateDefinitionAsync(new(tenantA, tenantA, NewId(), content, Now), timeout.Token);
                Assert.Equal("Platform Engineering", definition.Team);
                Assert.Equal("Backend", definition.Specialty);
                await Assert.ThrowsAsync<AgentDefinitionAdminException>(() =>
                    agents.CreateDefinitionAsync(new(tenantA, tenantA, NewId(), content with { Key = "ghost-team", Team = "Ghost Team" }, Now), timeout.Token));
                await Assert.ThrowsAsync<AgentDefinitionAdminException>(() =>
                    agents.CreateDefinitionAsync(new(tenantA, tenantA, NewId(), content with { Key = "ghost-spec", Specialty = "Ghost Specialty" }, Now), timeout.Token));

                // The specialty and team are now referenced by the definition -> both deletions conflict.
                await Assert.ThrowsAsync<TeamSpecialtyCatalogConflictException>(() =>
                    catalog.DeleteSpecialtyAsync(new(tenantA, tenantA, specialty.Id, Now), timeout.Token));
                await Assert.ThrowsAsync<TeamSpecialtyCatalogConflictException>(() =>
                    catalog.DeleteTeamAsync(new(tenantA, tenantA, team.Id, Now), timeout.Token));

                // Once nothing references them, deletion succeeds (specialty first, then its team).
                await agents.DeleteDefinitionAsync(new(tenantA, tenantA, definition.Id, Now), timeout.Token);
                await catalog.DeleteSpecialtyAsync(new(tenantA, tenantA, specialty.Id, Now), timeout.Token);
                Assert.Null(await catalog.GetSpecialtyAsync(tenantA, specialty.Id, timeout.Token));
                await catalog.DeleteTeamAsync(new(tenantA, tenantA, team.Id, Now), timeout.Token);
                Assert.Null(await catalog.GetTeamAsync(tenantA, team.Id, timeout.Token));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string NewId() => UlidValue.New(DateTimeOffset.UtcNow).ToString();

    private static async Task InsertTenantAsync(SqliteWriteDispatcher dispatcher, string tenantId, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tenants(id,name,version,created_at) VALUES($id,$name,0,$at);";
            command.Parameters.AddWithValue("$id", tenantId);
            command.Parameters.AddWithValue("$name", "Tenant " + tenantId);
            command.Parameters.AddWithValue("$at", Now.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);
}
