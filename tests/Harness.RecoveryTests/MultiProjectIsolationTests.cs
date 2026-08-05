using Harness.Persistence.Abstractions.Graph;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Graph;

namespace Harness.RecoveryTests;

/// <summary>
/// Dual Project Gate, Parte N — DOIS projetos no MESMO banco, zero contaminação: requisitos,
/// cards, anexos e grafo de A nunca aparecem nas consultas de B, e vice-versa. Com dois
/// projetos REAIS rodando em paralelo, um vazamento aqui seria uma mensagem/artefato/dependência
/// entregue ao produto errado — com autoridade.
/// </summary>
public sealed class MultiProjectIsolationTests : IAsyncLifetime
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5N01";
    private const string Org = "01ARZ3NDEKTSV4RRFFQ69G5N02";
    private const string ProjectA = "01ARZ3NDEKTSV4RRFFQ69G5NA1";
    private const string ProjectB = "01ARZ3NDEKTSV4RRFFQ69G5NB1";
    private const string Profile = "01ARZ3NDEKTSV4RRFFQ69G5N03";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 16, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-multiproj-{Guid.NewGuid():N}");

    private SqliteWriteDispatcher _dispatcher = null!;
    private SqliteWorkBoardStore _board = null!;
    private SqliteProjectGraphStore _graph = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(_root, "multi.db"));
        await SqliteMigrationRunner.ApplyAsync(_dispatcher, CancellationToken.None);
        await _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                $"""
                INSERT INTO tenants (id,name,created_at) VALUES ('{Tenant}','t','2026-08-05T00:00:00Z');
                INSERT INTO organizations (id,tenant_id,name,created_at)
                    VALUES ('{Org}','{Tenant}','TrensRJ','2026-08-05T00:00:00Z');
                INSERT INTO projects (id,tenant_id,organization_id,name,created_at)
                    VALUES ('{ProjectA}','{Tenant}','{Org}','Prisma','2026-08-05T00:00:00Z');
                INSERT INTO projects (id,tenant_id,organization_id,name,created_at)
                    VALUES ('{ProjectB}','{Tenant}','{Org}','Indicadores','2026-08-05T00:00:00Z');
                INSERT INTO local_users (id,tenant_id,display_name,created_at)
                    VALUES ('{Profile}','{Tenant}','gate','2026-08-05T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);
        _board = new SqliteWorkBoardStore(_dispatcher);
        _graph = new SqliteProjectGraphStore(_dispatcher);
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
    public async Task RequisitosCardsESolicitacoesNuncaAtravessamProjetos()
    {
        await SeedWorldAsync(ProjectA, "A");
        await SeedWorldAsync(ProjectB, "B");

        var solicitationsA = await _board.ListSolicitationsAsync(Tenant, ProjectA, null, 100, CancellationToken.None);
        var demandsB = await _board.ListDemandsAsync(Tenant, ProjectB, null, null, 100, CancellationToken.None);
        var tasksA = await _board.ListTasksAsync(Tenant, ProjectA, null, null, 100, CancellationToken.None);

        Assert.All(solicitationsA, s => Assert.Equal(ProjectA, s.ProjectId));
        Assert.All(demandsB, d => Assert.Equal(ProjectB, d.ProjectId));
        Assert.All(tasksA, t => Assert.Equal(ProjectA, t.ProjectId));
        Assert.DoesNotContain(tasksA, t => t.Title.Contains("[B]", StringComparison.Ordinal));
        Assert.DoesNotContain(demandsB, d => d.Title.Contains("[A]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OGrafoDeUmProjetoNuncaContemNosDoOutro()
    {
        var nodeA = new GraphNode(
            "requirement:req-a", ProjectA, GraphNodeType.Requirement, "req-a", "demand", 1,
            GraphNodeState.Active, GraphProvenance.Deterministic, 1.0, "Requisito do Prisma", Now, Now);
        var nodeB = new GraphNode(
            "requirement:req-b", ProjectB, GraphNodeType.Requirement, "req-b", "demand", 1,
            GraphNodeState.Active, GraphProvenance.Deterministic, 1.0, "Requisito de Indicadores", Now, Now);
        await _graph.ApplyAsync(
            new ProjectGraphApplyCommand(Tenant, ProjectA, [nodeA], [], "gate", Now),
            CancellationToken.None);
        await _graph.ApplyAsync(
            new ProjectGraphApplyCommand(Tenant, ProjectB, [nodeB], [], "gate", Now),
            CancellationToken.None);

        var graphA = await _graph.GetAsync(Tenant, ProjectA);
        var graphB = await _graph.GetAsync(Tenant, ProjectB);

        Assert.Equal("requirement:req-a", Assert.Single(graphA.Nodes).Id);
        Assert.Equal("requirement:req-b", Assert.Single(graphB.Nodes).Id);
        // Versões independentes: aplicar em B não avança a versão de A.
        Assert.Equal(1, graphA.Version);
        Assert.Equal(1, graphB.Version);

        // E o STALE de um projeto não respinga no outro: marcar em A deixa B intocado.
        await _graph.MarkStaleAsync(
            new ProjectGraphStaleCommand(
                Tenant, ProjectA, [new GraphStaleMark("requirement:req-a", "decision:x", 2)], Now),
            CancellationToken.None);
        var untouchedB = await _graph.GetAsync(Tenant, ProjectB);
        Assert.Equal(GraphNodeState.Active, Assert.Single(untouchedB.Nodes).State);
    }

    private async Task SeedWorldAsync(string projectId, string tag)
    {
        var suffix = tag == "A" ? "A" : "B";
        var solicitation = $"01ARZ3NDEKTSV4RRFFQ69G5{suffix}10";
        await _board.CreateSolicitationAsync(
            new BoardSolicitationCreateCommand(
                Tenant, solicitation, projectId, Profile, "request",
                $"[{tag}] Fonte", "Corpo.", null, Now),
            CancellationToken.None);
        await _board.CreateDemandAsync(
            new BoardDemandCreateCommand(
                Tenant, $"01ARZ3NDEKTSV4RRFFQ69G5{suffix}20", projectId, solicitation,
                solicitation, Profile, $"[{tag}] Requisito", "Descrição.", "high", Now),
            CancellationToken.None);
        await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                Tenant, $"01ARZ3NDEKTSV4RRFFQ69G5{suffix}30", projectId, null,
                $"01ARZ3NDEKTSV4RRFFQ69G5{suffix}31", $"01ARZ3NDEKTSV4RRFFQ69G5{suffix}32",
                Profile, $"[{tag}] Card", "high", null, null,
                $"01ARZ3NDEKTSV4RRFFQ69G5{suffix}33", "Instrução.", Now),
            CancellationToken.None);
    }
}
