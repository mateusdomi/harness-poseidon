using System.Globalization;
using Harness.Host.Architecture;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Architecture;

/// <summary>
/// UX-PROTO/ARCH: prova em SQLite in-process de que o <see cref="ArchitectureSelfMapSeeder"/> preenche
/// o Architecture Hub do projeto Poseidon (id fixo) com o MAPA derivado da estrutura REAL do repo —
/// elementos vigentes + relacionamentos vigentes — de forma IDEMPOTENTE (rodar 2x não duplica), e é
/// um no-op quando o projeto Poseidon não existe no tenant.
/// </summary>
public sealed class ArchitectureSelfMapSeederTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-24T10:00:00Z", CultureInfo.InvariantCulture);

    // 5 elementos de topo (system/containers/dataStore) + 9 módulos de domínio (component).
    private const int ExpectedElements = 14;

    // 4 (system contains containers) + 9 (host contains módulos) + 3 (integração) arestas.
    private const int ExpectedRelationships = 16;

    [Fact]
    public async Task SeedsRealMapAndIsIdempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"arch-selfmap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "arch.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);

                var tenantId = UlidValue.New(Now).ToString();
                var profileId = UlidValue.New(Now.AddMilliseconds(1)).ToString();
                await InsertTenantAsync(dispatcher, tenantId, timeout.Token);
                await InsertProfileAsync(dispatcher, tenantId, profileId, timeout.Token);

                var organizations = new SqliteOrganizationStore(dispatcher);
                var projects = new SqliteProjectStore(dispatcher);
                var store = new SqliteArchitectureStore(dispatcher);

                var organizationId = UlidValue.New(Now.AddMilliseconds(2)).ToString();
                _ = await organizations.CreateAsync(
                    new OrganizationCreateCommand(
                        tenantId, organizationId, "Grupo Poseidon", "grupo-poseidon", "personal",
                        new OrganizationBrandRecord(null, null, null, null), Now),
                    timeout.Token);

                var chiefAgentId = UlidValue.New(Now.AddMilliseconds(4)).ToString();
                _ = await projects.CreateAsync(
                    new ProjectCreateCommand(
                        tenantId,
                        new ProjectRecord(
                            tenantId, ArchitectureSelfMapSeeder.PoseidonProjectId, organizationId,
                            "Poseidon", "POS", "Control plane", "active", "medium", null, "local", "develop",
                            [], new ProjectBrandRecord(null, null, null, null),
                            [profileId], 1, chiefAgentId, "manual", Now, Now, 0),
                        Now.AddMilliseconds(5)),
                    timeout.Token);

                // Antes: Hub vazio (0 elementos) — a queixa do dono.
                Assert.Empty(await store.ListElementsAsync(
                    tenantId, ArchitectureSelfMapSeeder.PoseidonProjectId,
                    ArchitectureKinds.Implemented, null, 200, timeout.Token));

                var seeder = new ArchitectureSelfMapSeeder(store, projects, new FixedClock(Now.AddMinutes(1)));

                // Primeira semeadura: cria o mapa completo.
                var first = await seeder.EnsureSeededAsync(tenantId, timeout.Token);
                Assert.Equal(ExpectedElements, first.ElementsCreated);
                Assert.Equal(ExpectedRelationships, first.RelationshipsCreated);

                var elements = await store.ListElementsAsync(
                    tenantId, ArchitectureSelfMapSeeder.PoseidonProjectId,
                    ArchitectureKinds.Implemented, null, 200, timeout.Token);
                var relationships = await store.ListRelationshipsAsync(
                    tenantId, ArchitectureSelfMapSeeder.PoseidonProjectId,
                    ArchitectureKinds.Implemented, null, 200, timeout.Token);
                Assert.Equal(ExpectedElements, elements.Count);
                Assert.Equal(ExpectedRelationships, relationships.Count);

                // Modelagem honesta: há exatamente 1 system (Poseidon), containers e um dataStore, e
                // todo relacionamento aponta para elementos que existem no modelo.
                Assert.Single(elements, e => e.Kind == ArchitectureKinds.SystemKind);
                Assert.Contains(elements, e => e.Kind == "dataStore");
                Assert.Contains(elements, e => e is { Kind: "container", Name: "Frontend (React SPA)" });
                Assert.Contains(elements, e => e is { Kind: "component", Name: "Módulo Architecture" });

                var ids = elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                Assert.All(relationships, r =>
                {
                    Assert.Contains(r.SourceId, ids);
                    Assert.Contains(r.TargetId, ids);
                    Assert.True(ArchitectureKinds.IsRelationshipKind(r.Kind));
                    Assert.Equal(ArchitectureSelfMapSeeder.PoseidonProjectId, r.ProjectId);
                });

                // Idempotência: reexecutar não cria nada e não duplica.
                var second = await seeder.EnsureSeededAsync(tenantId, timeout.Token);
                Assert.False(second.ChangedAnything);
                Assert.Equal(0, second.ElementsCreated);
                Assert.Equal(0, second.RelationshipsCreated);
                Assert.Equal(ExpectedElements, (await store.ListElementsAsync(
                    tenantId, ArchitectureSelfMapSeeder.PoseidonProjectId,
                    ArchitectureKinds.Implemented, null, 200, timeout.Token)).Count);
                Assert.Equal(ExpectedRelationships, (await store.ListRelationshipsAsync(
                    tenantId, ArchitectureSelfMapSeeder.PoseidonProjectId,
                    ArchitectureKinds.Implemented, null, 200, timeout.Token)).Count);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IsNoOpWhenPoseidonProjectIsAbsent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"arch-selfmap-absent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "absent.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                var tenantId = UlidValue.New(Now).ToString();
                await InsertTenantAsync(dispatcher, tenantId, timeout.Token);

                var store = new SqliteArchitectureStore(dispatcher);
                var seeder = new ArchitectureSelfMapSeeder(
                    store, new SqliteProjectStore(dispatcher), new FixedClock(Now.AddMinutes(1)));

                var result = await seeder.EnsureSeededAsync(tenantId, timeout.Token);
                Assert.False(result.ChangedAnything);
                Assert.Empty(await store.ListElementsAsync(
                    tenantId, ArchitectureSelfMapSeeder.PoseidonProjectId,
                    ArchitectureKinds.Implemented, null, 200, timeout.Token));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task InsertTenantAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tenants(id,name,version,created_at) VALUES($id,$name,0,$at);";
            command.Parameters.AddWithValue("$id", tenantId);
            command.Parameters.AddWithValue("$name", "Tenant " + tenantId);
            command.Parameters.AddWithValue("$at", Store(Now));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private static async Task InsertProfileAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, string profileId, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO local_users(id,tenant_id,display_name,version,created_at) VALUES($id,$tenant,$name,0,$at);";
            command.Parameters.AddWithValue("$id", profileId);
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$name", "Operator");
            command.Parameters.AddWithValue("$at", Store(Now));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        private long _tick;
        public DateTimeOffset UtcNow => now.AddTicks(Interlocked.Increment(ref _tick));
    }
}
