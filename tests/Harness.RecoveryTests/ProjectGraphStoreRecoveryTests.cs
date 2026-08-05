using Harness.Persistence.Abstractions.Graph;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Graph;

namespace Harness.RecoveryTests;

/// <summary>
/// Onda 1 — a store da ProjectGraphProjection sobre banco REAL (migrações aplicadas):
/// roundtrip fiel, versão monotônica, apply idempotente, e a semântica operacional do STALE
/// (preservado quando a fonte não mudou; limpo quando a fonte avançou de versão — a mudança da
/// própria fonte era a revalidação que o STALE esperava).
/// </summary>
public sealed class ProjectGraphStoreRecoveryTests : IAsyncLifetime
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5G01";
    private const string Org = "01ARZ3NDEKTSV4RRFFQ69G5G02";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5G03";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-graphstore-{Guid.NewGuid():N}");

    private SqliteWriteDispatcher _dispatcher = null!;
    private SqliteProjectGraphStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(_root, "graph.db"));
        await SqliteMigrationRunner.ApplyAsync(_dispatcher, CancellationToken.None);
        await _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                $"""
                INSERT INTO tenants (id,name,created_at) VALUES ('{Tenant}','t','2026-08-05T00:00:00Z');
                INSERT INTO organizations (id,tenant_id,name,created_at)
                    VALUES ('{Org}','{Tenant}','o','2026-08-05T00:00:00Z');
                INSERT INTO projects (id,tenant_id,organization_id,name,created_at)
                    VALUES ('{Project}','{Tenant}','{Org}','p','2026-08-05T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);
        _store = new SqliteProjectGraphStore(_dispatcher);
    }

    public async Task DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza de temp não decide teste.
        }
    }

    [Fact]
    public async Task RoundtripFielEVersaoMonotonica()
    {
        var node = Node("card:c1", GraphNodeType.Card, "c1", version: 1);
        var requirement = Node("requirement:r1", GraphNodeType.Requirement, "r1", version: 1);
        var edge = GraphEdge.Structural("card:c1", "requirement:r1", GraphRelationType.Implements, Now);

        var v1 = await _store.ApplyAsync(
            new ProjectGraphApplyCommand(Tenant, Project, [node, requirement], [edge], "teste", Now));
        var v2 = await _store.ApplyAsync(
            new ProjectGraphApplyCommand(Tenant, Project, [node, requirement], [edge], "reaplicação", Now));

        Assert.Equal(1, v1);
        Assert.Equal(2, v2);

        var snapshot = await _store.GetAsync(Tenant, Project);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(2, snapshot.Nodes.Count);
        var stored = Assert.Single(snapshot.Edges);
        Assert.Equal(GraphProvenance.Deterministic, stored.Provenance);
        Assert.Equal(1.0, stored.Confidence);
        Assert.Equal(GraphEdgeStatus.Accepted, stored.Status);
    }

    [Fact]
    public async Task NoQueSumiuDasFontesERemovidoNoApply()
    {
        var a = Node("card:a", GraphNodeType.Card, "a", 1);
        var b = Node("card:b", GraphNodeType.Card, "b", 1);
        await _store.ApplyAsync(new ProjectGraphApplyCommand(Tenant, Project, [a, b], [], "dois", Now));

        await _store.ApplyAsync(new ProjectGraphApplyCommand(Tenant, Project, [a], [], "um", Now));

        var snapshot = await _store.GetAsync(Tenant, Project);
        var survivor = Assert.Single(snapshot.Nodes);
        Assert.Equal("card:a", survivor.Id);
    }

    [Fact]
    public async Task StaleSobreviveAoApplyQueNaoMudouAFonteECaiQuandoElaAvanca()
    {
        var card = Node("card:c1", GraphNodeType.Card, "c1", version: 1);
        var requirement = Node("requirement:r1", GraphNodeType.Requirement, "r1", version: 3);
        await _store.ApplyAsync(
            new ProjectGraphApplyCommand(Tenant, Project, [card, requirement], [], "base", Now));

        await _store.MarkStaleAsync(new ProjectGraphStaleCommand(
            Tenant, Project, [new GraphStaleMark("card:c1", "requirement:r1", 3)], Now));

        // Rebuild com a MESMA versão do card: o STALE (estado operacional) é preservado, com causa.
        await _store.ApplyAsync(
            new ProjectGraphApplyCommand(Tenant, Project, [card, requirement], [], "rebuild", Now));
        var preserved = await _store.GetAsync(Tenant, Project);
        var stale = Assert.Single(preserved.Nodes, n => n.Id == "card:c1");
        Assert.Equal(GraphNodeState.Stale, stale.State);
        Assert.Equal("requirement:r1", stale.StaleCauseNodeId);
        Assert.Equal(3, stale.StaleCauseVersion);

        // A fonte AVANÇOU (instrução nova do card): a mudança era a revalidação esperada.
        await _store.ApplyAsync(
            new ProjectGraphApplyCommand(
                Tenant, Project, [card with { Version = 2 }, requirement], [], "avanço", Now));
        var advanced = await _store.GetAsync(Tenant, Project);
        var active = Assert.Single(advanced.Nodes, n => n.Id == "card:c1");
        Assert.Equal(GraphNodeState.Active, active.State);
        Assert.Null(active.StaleCauseNodeId);
    }

    [Fact]
    public async Task ClearStaleRegistraARevalidacaoAprovada()
    {
        var card = Node("card:c1", GraphNodeType.Card, "c1", 1);
        await _store.ApplyAsync(new ProjectGraphApplyCommand(Tenant, Project, [card], [], "base", Now));
        await _store.MarkStaleAsync(new ProjectGraphStaleCommand(
            Tenant, Project, [new GraphStaleMark("card:c1", "requirement:r9", 2)], Now));

        await _store.ClearStaleAsync(Tenant, Project, "card:c1", Now.AddMinutes(5));

        var snapshot = await _store.GetAsync(Tenant, Project);
        Assert.Equal(GraphNodeState.Active, Assert.Single(snapshot.Nodes).State);
    }

    private static GraphNode Node(
        string id, GraphNodeType type, string sourceId, int version) =>
        new(id, Project, type, sourceId, "teste", version, GraphNodeState.Active,
            GraphProvenance.Deterministic, 1.0, $"Nó {sourceId}", Now, Now);
}
