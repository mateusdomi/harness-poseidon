using Harness.Host.Graph;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.RecoveryTests;

/// <summary>
/// Prisma Launch Gate — a prova de que 0/26 de cobertura NÃO é deadlock prematuro.
///
/// Com o grafo LIGADO, "requisito ainda sem card porque o Planning nem aconteceu" não pode virar
/// "nada do Playbook executa". O readiness só segue arestas de INSUMO que saem do card
/// (depends_on/blocked_by/derives_from): requisito descoberto não é insumo de card nenhum, então
/// trabalho de fase inicial (triagem, discovery, arquitetura, planejamento) despacha normalmente.
/// O que fica bloqueado é exatamente o fora-de-ordem: implementação cujo predecessor declarado
/// está incompleto.
/// </summary>
public sealed class GraphEarlyPhaseDeadlockTests : IAsyncLifetime
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5H01";
    private const string Org = "01ARZ3NDEKTSV4RRFFQ69G5H02";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5H03";
    private const string Profile = "01ARZ3NDEKTSV4RRFFQ69G5H04";
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-earlyphase-{Guid.NewGuid():N}");

    private SqliteWriteDispatcher _dispatcher = null!;
    private SqliteWorkBoardStore _board = null!;
    private SqliteProjectGraphStore _graphStore = null!;
    private ProjectGraphProjectionService _projection = null!;
    private ProjectGraphImpactService _impact = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(_root, "gate.db"));
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
                    VALUES ('{Project}','{Tenant}','{Org}','prisma-gate','2026-08-05T00:00:00Z');
                INSERT INTO local_users (id,tenant_id,display_name,created_at)
                    VALUES ('{Profile}','{Tenant}','gate','2026-08-05T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);
        _board = new SqliteWorkBoardStore(_dispatcher);
        _graphStore = new SqliteProjectGraphStore(_dispatcher);
        _projection = new ProjectGraphProjectionService(
            _board, _graphStore, new StubClock(Now), new ProjectGraphOptions(ProjectionEnabled: true));
        _impact = new ProjectGraphImpactService(
            _projection, _graphStore, _board, new StubClock(Now),
            NullLogger<ProjectGraphImpactService>.Instance);
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
    public async Task RequisitosSemCardNaoSeguramTrabalhoDeFaseInicialMasForaDeOrdemFicaPreso()
    {
        // O mundo do Prisma pré-planejamento: solicitação, demandas (requisitos) SEM card, e o
        // primeiro card documental de fase inicial.
        var solicitationId = "01ARZ3NDEKTSV4RRFFQ69G5H10";
        await _board.CreateSolicitationAsync(
            new BoardSolicitationCreateCommand(
                Tenant, solicitationId, Project, Profile, "request",
                "Especificação do Prisma", "Requisitos completos, Oracle 19c.", null, Now),
            CancellationToken.None);
        for (var index = 1; index <= 3; index++)
        {
            await _board.CreateDemandAsync(
                new BoardDemandCreateCommand(
                    Tenant, $"01ARZ3NDEKTSV4RRFFQ69G5H2{index}", Project, solicitationId,
                    solicitationId, Profile, $"Critério T{index}", "Given/When/Then.",
                    "high", Now.AddMinutes(index)),
                CancellationToken.None);
        }

        var triagem = "01ARZ3NDEKTSV4RRFFQ69G5H30";
        await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                Tenant, triagem, Project, null, "01ARZ3NDEKTSV4RRFFQ69G5H32", "01ARZ3NDEKTSV4RRFFQ69G5H33",
                Profile, "1-Triagem — Ficha de Demanda Qualificada", "high", null, null,
                "01ARZ3NDEKTSV4RRFFQ69G5H31", "Elaborar a ficha.", Now.AddMinutes(10),
                "1-Triagem", "documento"),
            CancellationToken.None);

        var rebuilt = await _projection.RebuildAsync(Tenant, Project);
        Assert.True(rebuilt.NodeCount >= 5);

        // FASE INICIAL LIBERADA: o card de triagem não tem aresta de insumo — os requisitos sem
        // cobertura NÃO entram nos fatos, e o readiness fica idêntico ao de sempre.
        var (stale, incomplete) = await _impact.InspectUpstreamAsync(Tenant, Project, triagem);
        Assert.Empty(stale);
        Assert.Empty(incomplete);
        var readiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
            "documento", HasInstruction: true, IsBlocked: false, stale, incomplete));
        Assert.True(readiness.IsDispatchable, string.Join(",", readiness.Blockers));

        // FORA DE ORDEM PRESO: um card de implementação que declara depender da triagem (ainda
        // incompleta) NÃO despacha, com o nó exato na razão.
        var implementacao = "01ARZ3NDEKTSV4RRFFQ69G5H40";
        await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                Tenant, implementacao, Project, null, "01ARZ3NDEKTSV4RRFFQ69G5H42", "01ARZ3NDEKTSV4RRFFQ69G5H43",
                Profile, "FEAT — Implementação do cadastro", "high", null, null,
                "01ARZ3NDEKTSV4RRFFQ69G5H41", "Implementar.", Now.AddMinutes(11),
                "5-Desenvolvimento", "agent_task"),
            CancellationToken.None);
        await _projection.RebuildAsync(Tenant, Project);

        // A dependência declarada entra como aresta estrutural (o replay importa do card_type
        // review; aqui o vínculo é declarado diretamente na projeção persistida).
        var snapshot = await _projection.GetSnapshotAsync(Tenant, Project);
        var withDependency = snapshot.Nodes.ToList();
        var edges = snapshot.Edges.Append(
            Harness.SharedKernel.Graph.GraphEdge.Structural(
                Harness.SharedKernel.Graph.GraphNode.DeterministicId(
                    Harness.SharedKernel.Graph.GraphNodeType.Card, implementacao),
                Harness.SharedKernel.Graph.GraphNode.DeterministicId(
                    Harness.SharedKernel.Graph.GraphNodeType.Card, triagem),
                Harness.SharedKernel.Graph.GraphRelationType.DependsOn, Now))
            .ToArray();
        await _graphStore.ApplyAsync(
            new Harness.Persistence.Abstractions.Graph.ProjectGraphApplyCommand(
                Tenant, Project, withDependency, edges, "gate:dependencia-declarada", Now),
            CancellationToken.None);

        var (staleDev, incompleteDev) = await _impact.InspectUpstreamAsync(
            Tenant, Project, implementacao);
        var devReadiness = CardReadinessEvaluator.Evaluate(new CardReadinessFacts(
            "agent_task", HasInstruction: true, IsBlocked: false, staleDev, incompleteDev));
        Assert.False(devReadiness.IsDispatchable);
        Assert.Contains(
            $"{CardReadinessEvaluator.UpstreamIncomplete}:card:{triagem}",
            devReadiness.Blockers);
    }

    /// <summary>
    /// Bloco 6 do Launch Gate — rollback: ON → OFF (comportamento anterior, projeção intacta no
    /// banco) → ON (rebuild consistente, versão monotônica segue).
    /// </summary>
    [Fact]
    public async Task RollbackDaFlagNaoDestroiAProjecaoEReligarContinuaConsistente()
    {
        var solicitationId = "01ARZ3NDEKTSV4RRFFQ69G5H50";
        await _board.CreateSolicitationAsync(
            new BoardSolicitationCreateCommand(
                Tenant, solicitationId, Project, Profile, "request",
                "Fonte", "Corpo.", null, Now),
            CancellationToken.None);
        var onBefore = await _projection.RebuildAsync(Tenant, Project);

        // OFF: o serviço se declara desligado e recusa; a projeção persiste intacta.
        var off = new ProjectGraphProjectionService(
            _board, _graphStore, new StubClock(Now), new ProjectGraphOptions(ProjectionEnabled: false));
        Assert.False(off.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => off.RebuildAsync(Tenant, Project));
        var persisted = await _graphStore.GetAsync(Tenant, Project);
        Assert.Equal(onBefore.Version, persisted.Version);
        Assert.Equal(onBefore.NodeCount, persisted.Nodes.Count);

        // ON de novo: rebuild produz o MESMO grafo (idempotência) e apenas avança a versão.
        var onAgain = await _projection.RebuildAsync(Tenant, Project);
        Assert.Equal(onBefore.NodeCount, onAgain.NodeCount);
        Assert.Equal(onBefore.EdgeCount, onAgain.EdgeCount);
        Assert.Equal(onBefore.Version + 1, onAgain.Version);
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
