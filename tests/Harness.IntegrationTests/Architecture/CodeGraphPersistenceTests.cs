using System.Globalization;
using Harness.Host.Architecture;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.CodeGraph;
using Harness.SharedKernel.Time;

namespace Harness.IntegrationTests.Architecture;

/// <summary>
/// F15/B6 — o índice ARMAZENADO por projeto, e o self-map alimentado por derivação.
///
/// O teste central é o da substituição: reconstruir tem de trocar o grafo inteiro, sem deixar resíduo
/// da derivação anterior. Um índice derivado que acumula linhas velhas mede impacto contra código que
/// já não existe — e responde com a segurança de quem mediu.
/// </summary>
public sealed class CodeGraphPersistenceTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FBV";
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FBW";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FBX";
    private const string CoordinationElement = "01ARZ3NDEKTSV4RRFFQ69G5FB1";
    private const string ProjectsElement = "01ARZ3NDEKTSV4RRFFQ69G5FB2";
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static CodeGraph GraphOf(params (string Node, string File, string Module)[] nodes) =>
        CodeGraph.Create(
            nodes.Select(item => new CodeGraphNode(
                item.Node, item.Node, CodeGraphNodeKind.Type, item.File, item.Module)),
            []);

    [Fact]
    public async Task ARebuildReplacesTheWholeGraphAndLeavesNoResidue()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCodeGraphStore(dispatcher);
            var first = CodeGraph.Create(
                [
                    new CodeGraphNode("T:A", "A", CodeGraphNodeKind.Type, "src/a.cs", "M"),
                    new CodeGraphNode("T:B", "B", CodeGraphNodeKind.Type, "src/b.cs", "M")
                ],
                [new CodeGraphEdge("T:B", "T:A", CodeGraphEdgeKind.References)]);

            await store.ReplaceAsync(Write(first), timeout.Token);

            // Segunda derivação: 'B' deixou de existir no código.
            var second = GraphOf(("T:A", "src/a.cs", "M"));
            var snapshot = await store.ReplaceAsync(Write(second), timeout.Token);

            Assert.Equal(1, snapshot.NodeCount);
            Assert.Equal(0, snapshot.EdgeCount);
            Assert.Equal(second.Digest, snapshot.Digest);

            var content = await store.LoadAsync(
                Tenant, Project, "csharp", timeout.Token);
            Assert.NotNull(content);
            // O nó e a aresta da derivação anterior não podem ter sobrado.
            Assert.Equal(["T:A"], content!.Nodes.Select(node => node.NodeId));
            Assert.Empty(content.Edges);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task TheStoredGraphComesBackIdenticalToTheDerivedOne()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCodeGraphStore(dispatcher);
            var derived = CodeGraph.Create(
                [
                    new CodeGraphNode("T:App.Nucleo", "App.Nucleo", CodeGraphNodeKind.Type, "src/nucleo.cs", "App"),
                    new CodeGraphNode("T:App.Borda", "App.Borda", CodeGraphNodeKind.Type, "src/borda.cs", "App"),
                    new CodeGraphNode("M:App", "App", CodeGraphNodeKind.Module, string.Empty, "App")
                ],
                [
                    new CodeGraphEdge("T:App.Borda", "T:App.Nucleo", CodeGraphEdgeKind.References),
                    new CodeGraphEdge("M:App", "T:App.Nucleo", CodeGraphEdgeKind.Contains)
                ]);
            await store.ReplaceAsync(Write(derived), timeout.Token);

            var content = await store.LoadAsync(Tenant, Project, "csharp", timeout.Token);
            var reloaded = CodeGraph.Create(
                content!.Nodes.Select(row => new CodeGraphNode(
                    row.NodeId, row.Symbol, (CodeGraphNodeKind)row.Kind, row.FilePath, row.Module)),
                content.Edges.Select(row => new CodeGraphEdge(
                    row.FromNodeId, row.ToNodeId, (CodeGraphEdgeKind)row.Kind)));

            // Igualdade de digest é a prova de que a ida e a volta não perderam nem inventaram nada.
            Assert.Equal(derived.Digest, reloaded.Digest);
            Assert.Equal(["T:App.Borda"], reloaded.DependentsOf("T:App.Nucleo"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task AProjectNeverIndexedAnswersUnknownAndNotEmpty()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var store = new SqliteCodeGraphStore(dispatcher);

            // Nulo, e não um grafo vazio: grafo vazio afirmaria "medi e não há nada".
            Assert.Null(await store.GetSnapshotAsync(Tenant, Project, "csharp", timeout.Token));
            Assert.Null(await store.LoadAsync(Tenant, Project, "csharp", timeout.Token));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task TheSelfMapGainsTheDerivedDependencyAndSyncingTwiceDoesNotDuplicate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var architecture = new SqliteArchitectureStore(dispatcher);
            await SeedElementsAsync(architecture, timeout.Token);
            var service = new CodeGraphDerivationService(
                new RoslynCodeGraphIndex(),
                new SqliteCodeGraphStore(dispatcher),
                architecture,
                new FixedClock(Now));

            // Coordination referencia Projects — dependência REAL entre os dois módulos.
            var graph = CodeGraph.Create(
                [
                    new CodeGraphNode(
                        "T:Coord.Politica", "Coord.Politica", CodeGraphNodeKind.Type,
                        "src/Modules/Harness.Modules.Coordination/Politica.cs",
                        "Harness.Modules.Coordination"),
                    new CodeGraphNode(
                        "T:Proj.Projeto", "Proj.Projeto", CodeGraphNodeKind.Type,
                        "src/Modules/Harness.Modules.Projects/Projeto.cs",
                        "Harness.Modules.Projects")
                ],
                [new CodeGraphEdge("T:Coord.Politica", "T:Proj.Projeto", CodeGraphEdgeKind.References)]);

            var created = await service.SyncSelfMapAsync(Tenant, Project, graph, timeout.Token);
            Assert.Equal(1, created);

            var relationships = await architecture.ListRelationshipsAsync(
                Tenant, Project, ArchitectureKinds.Implemented, null, 100, timeout.Token);
            var derived = Assert.Single(relationships);
            Assert.Equal(CoordinationElement, derived.SourceId);
            Assert.Equal(ProjectsElement, derived.TargetId);
            Assert.Equal("depends-on", derived.Kind);
            // A marca é o que distingue seta MEDIDA de seta declarada à mão no mesmo mapa.
            Assert.Equal("code-graph", derived.Properties["derivedFrom"]);

            // Idempotência: o id da relação é determinístico a partir do par.
            Assert.Equal(0, await service.SyncSelfMapAsync(Tenant, Project, graph, timeout.Token));
            var afterResync = await architecture.ListRelationshipsAsync(
                Tenant, Project, ArchitectureKinds.Implemented, null, 100, timeout.Token);
            Assert.Single(afterResync);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task AModuleWithoutAnElementInTheMapIsOmittedInsteadOfInvented()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, dispatcher) = await CreateDatabaseAsync(timeout.Token);
        try
        {
            var architecture = new SqliteArchitectureStore(dispatcher);
            await SeedElementsAsync(architecture, timeout.Token);
            var service = new CodeGraphDerivationService(
                new RoslynCodeGraphIndex(),
                new SqliteCodeGraphStore(dispatcher),
                architecture,
                new FixedClock(Now));

            var graph = CodeGraph.Create(
                [
                    new CodeGraphNode(
                        "T:Coord.Politica", "Coord.Politica", CodeGraphNodeKind.Type,
                        "a.cs", "Harness.Modules.Coordination"),
                    new CodeGraphNode(
                        "T:Nada.Tipo", "Nada.Tipo", CodeGraphNodeKind.Type,
                        "b.cs", "Harness.Modules.ModuloQueNaoEstaNoMapa")
                ],
                [new CodeGraphEdge("T:Coord.Politica", "T:Nada.Tipo", CodeGraphEdgeKind.References)]);

            // Mapa com nó fantasma é pior que mapa incompleto: parece completo.
            Assert.Equal(0, await service.SyncSelfMapAsync(Tenant, Project, graph, timeout.Token));
            var relationships = await architecture.ListRelationshipsAsync(
                Tenant, Project, ArchitectureKinds.Implemented, null, 100, timeout.Token);
            Assert.Empty(relationships);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static CodeGraphWrite Write(CodeGraph graph) => new(
        Tenant,
        Project,
        "csharp",
        graph.Digest,
        FilesIndexed: graph.Nodes.Count,
        CodeGraphSnapshot.SyntaxOnlyScope,
        ErrorCount: 0,
        SourceRevision: "abc1234",
        Now,
        [.. graph.Nodes.Select(node => new CodeGraphNodeRow(
            node.NodeId, node.Symbol, (int)node.Kind, node.FilePath, node.Module))],
        [.. graph.Edges.Select(edge => new CodeGraphEdgeRow(
            edge.FromNodeId, edge.ToNodeId, (int)edge.Kind))]);

    private static async Task SeedElementsAsync(
        SqliteArchitectureStore store, CancellationToken token)
    {
        foreach (var (id, name) in ((string, string)[])
                 [(CoordinationElement, "Coordination"), (ProjectsElement, "Projects")])
        {
            await store.CreateElementAsync(
                new ArchitectureElementRecord(
                    Tenant, id, Project, "component", name, name,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    ArchitectureKinds.Implemented, false, 1, null, null, null, Now, Now),
                token);
        }
    }

    private static async Task<(string Root, SqliteWriteDispatcher Dispatcher)> CreateDatabaseAsync(
        CancellationToken token)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f15-code-graph",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(
            Path.Combine(root, "codegraph.db"), token);
        await SqliteMigrationRunner.ApplyAsync(dispatcher, token);
        await dispatcher.ExecuteAsync(
            async (connection, inner) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     INSERT INTO tenants (id, name, created_at)
                     VALUES ('{Tenant}', 'Tenant', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO organizations (id, tenant_id, name, created_at)
                     VALUES ('{Organization}', '{Tenant}', 'Organization', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                     VALUES ('{Project}', '{Tenant}', '{Organization}', 'Project', '2026-07-29T12:00:00.0000000+00:00');
                     """;
                await command.ExecuteNonQueryAsync(inner);
                return 0;
            },
            token);
        return (root, dispatcher);
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Artefato em disco não é resultado.
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
