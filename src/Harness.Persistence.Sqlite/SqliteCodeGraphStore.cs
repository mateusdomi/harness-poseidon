using System.Globalization;
using Harness.Persistence.Abstractions.Architecture;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Índice de grafo de código em SQLite (ver a migration 0103 para o porquê de a escrita ser sempre
/// SUBSTITUIÇÃO integral).
///
/// A substituição acontece numa transação única — apagar e inserir em passos separados deixaria uma
/// leitura concorrente ver meio grafo novo com meio grafo velho, e um raio de impacto calculado sobre
/// essa mistura estaria errado sem nenhum sinal de que está.
/// </summary>
public sealed class SqliteCodeGraphStore(SqliteWriteDispatcher dispatcher) : ICodeGraphStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private const string SelectSnapshot =
        "SELECT tenant_id,project_id,language,digest,node_count,edge_count,files_indexed," +
        "diagnostic_scope,error_count,source_revision,built_at FROM code_graph_snapshots";

    public Task<CodeGraphSnapshot?> GetSnapshotAsync(
        string tenantId,
        string projectId,
        string language,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            (connection, token) => ReadSnapshotAsync(
                connection, null, tenantId, projectId, language, token),
            cancellationToken);

    public Task<CodeGraphSnapshot> ReplaceAsync(
        CodeGraphWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Language);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Digest);

        return _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);

                // A ordem importa: as filhas têm ON DELETE CASCADE, mas apagar explicitamente antes
                // deixa o passo legível e não depende de o pragma de chave estrangeira estar ligado.
                foreach (var table in (string[])["code_graph_edges", "code_graph_nodes", "code_graph_snapshots"])
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = tx;
                    delete.CommandText =
                        $"DELETE FROM {table} WHERE tenant_id=$tenant AND project_id=$project " +
                        "AND language=$language;";
                    Add(delete, "$tenant", write.TenantId);
                    Add(delete, "$project", write.ProjectId);
                    Add(delete, "$language", write.Language);
                    await delete.ExecuteNonQueryAsync(token);
                }

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText =
                        "INSERT INTO code_graph_snapshots (tenant_id,project_id,language,digest," +
                        "node_count,edge_count,files_indexed,diagnostic_scope,error_count," +
                        "source_revision,built_at) VALUES ($tenant,$project,$language,$digest," +
                        "$nodes,$edges,$files,$scope,$errors,$revision,$builtAt);";
                    Add(insert, "$tenant", write.TenantId);
                    Add(insert, "$project", write.ProjectId);
                    Add(insert, "$language", write.Language);
                    Add(insert, "$digest", write.Digest);
                    Add(insert, "$nodes", write.Nodes.Count);
                    Add(insert, "$edges", write.Edges.Count);
                    Add(insert, "$files", write.FilesIndexed);
                    Add(insert, "$scope", write.DiagnosticScope);
                    Add(insert, "$errors", write.ErrorCount);
                    AddNullable(insert, "$revision", write.SourceRevision);
                    Add(insert, "$builtAt", Store(write.BuiltAt));
                    await insert.ExecuteNonQueryAsync(token);
                }

                await using (var node = connection.CreateCommand())
                {
                    node.Transaction = tx;
                    node.CommandText =
                        "INSERT INTO code_graph_nodes (tenant_id,project_id,language,node_id," +
                        "symbol,kind,file_path,module) VALUES ($tenant,$project,$language,$node," +
                        "$symbol,$kind,$file,$module);";
                    Add(node, "$tenant", write.TenantId);
                    Add(node, "$project", write.ProjectId);
                    Add(node, "$language", write.Language);
                    var nodeId = node.Parameters.Add("$node", SqliteType.Text);
                    var symbol = node.Parameters.Add("$symbol", SqliteType.Text);
                    var kind = node.Parameters.Add("$kind", SqliteType.Integer);
                    var file = node.Parameters.Add("$file", SqliteType.Text);
                    var module = node.Parameters.Add("$module", SqliteType.Text);
                    foreach (var row in write.Nodes)
                    {
                        nodeId.Value = row.NodeId;
                        symbol.Value = row.Symbol;
                        kind.Value = row.Kind;
                        file.Value = row.FilePath;
                        module.Value = row.Module;
                        await node.ExecuteNonQueryAsync(token);
                    }
                }

                await using (var edge = connection.CreateCommand())
                {
                    edge.Transaction = tx;
                    edge.CommandText =
                        "INSERT INTO code_graph_edges (tenant_id,project_id,language,from_node_id," +
                        "to_node_id,kind) VALUES ($tenant,$project,$language,$from,$to,$kind);";
                    Add(edge, "$tenant", write.TenantId);
                    Add(edge, "$project", write.ProjectId);
                    Add(edge, "$language", write.Language);
                    var from = edge.Parameters.Add("$from", SqliteType.Text);
                    var to = edge.Parameters.Add("$to", SqliteType.Text);
                    var kind = edge.Parameters.Add("$kind", SqliteType.Integer);
                    foreach (var row in write.Edges)
                    {
                        from.Value = row.FromNodeId;
                        to.Value = row.ToNodeId;
                        kind.Value = row.Kind;
                        await edge.ExecuteNonQueryAsync(token);
                    }
                }

                await tx.CommitAsync(token);
                return (await ReadSnapshotAsync(
                    connection, null, write.TenantId, write.ProjectId, write.Language, token))!;
            },
            cancellationToken);
    }

    public Task<CodeGraphContent?> LoadAsync(
        string tenantId,
        string projectId,
        string language,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                var snapshot = await ReadSnapshotAsync(
                    connection, null, tenantId, projectId, language, token);
                if (snapshot is null)
                {
                    return null;
                }

                var nodes = new List<CodeGraphNodeRow>();
                await using (var query = connection.CreateCommand())
                {
                    query.CommandText =
                        "SELECT node_id,symbol,kind,file_path,module FROM code_graph_nodes " +
                        "WHERE tenant_id=$tenant AND project_id=$project AND language=$language " +
                        "ORDER BY node_id;";
                    Add(query, "$tenant", tenantId);
                    Add(query, "$project", projectId);
                    Add(query, "$language", language);
                    await using var reader = await query.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        nodes.Add(new CodeGraphNodeRow(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetInt32(2),
                            reader.GetString(3),
                            reader.GetString(4)));
                    }
                }

                var edges = new List<CodeGraphEdgeRow>();
                await using (var query = connection.CreateCommand())
                {
                    query.CommandText =
                        "SELECT from_node_id,to_node_id,kind FROM code_graph_edges " +
                        "WHERE tenant_id=$tenant AND project_id=$project AND language=$language " +
                        "ORDER BY from_node_id,to_node_id,kind;";
                    Add(query, "$tenant", tenantId);
                    Add(query, "$project", projectId);
                    Add(query, "$language", language);
                    await using var reader = await query.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        edges.Add(new CodeGraphEdgeRow(
                            reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
                    }
                }

                return new CodeGraphContent(snapshot, nodes, edges);
            },
            cancellationToken);

    private static async Task<CodeGraphSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string tenantId,
        string projectId,
        string language,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText =
            $"{SelectSnapshot} WHERE tenant_id=$tenant AND project_id=$project AND language=$language;";
        Add(query, "$tenant", tenantId);
        Add(query, "$project", projectId);
        Add(query, "$language", language);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static CodeGraphSnapshot Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetString(7),
        reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
