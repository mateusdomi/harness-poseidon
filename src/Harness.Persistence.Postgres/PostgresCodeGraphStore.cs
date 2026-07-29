using Harness.Persistence.Abstractions.Architecture;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Índice de grafo de código em PostgreSQL. Paridade estrita com o SQLite, inclusive na transação
/// única da substituição: apagar e inserir em passos separados deixaria uma leitura concorrente ver
/// meio grafo novo com meio grafo velho.
/// </summary>
public sealed class PostgresCodeGraphStore(NpgsqlDataSource dataSource) : ICodeGraphStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string SelectSnapshot =
        "SELECT tenant_id,project_id,language,digest,node_count,edge_count,files_indexed," +
        "diagnostic_scope,error_count,source_revision,built_at FROM harness.code_graph_snapshots";

    public async Task<CodeGraphSnapshot?> GetSnapshotAsync(
        string tenantId,
        string projectId,
        string language,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadSnapshotAsync(
            connection, null, tenantId, projectId, language, cancellationToken);
    }

    public async Task<CodeGraphSnapshot> ReplaceAsync(
        CodeGraphWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Language);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Digest);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var table in (string[])
                 ["harness.code_graph_edges", "harness.code_graph_nodes", "harness.code_graph_snapshots"])
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText =
                $"DELETE FROM {table} WHERE tenant_id=$1 AND project_id=$2 AND language=$3;";
            delete.Parameters.Add(Text(write.TenantId));
            delete.Parameters.Add(Text(write.ProjectId));
            delete.Parameters.Add(Text(write.Language));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO harness.code_graph_snapshots
                (tenant_id,project_id,language,digest,node_count,edge_count,files_indexed,
                 diagnostic_scope,error_count,source_revision,built_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11);
                """;
            insert.Parameters.Add(Text(write.TenantId));
            insert.Parameters.Add(Text(write.ProjectId));
            insert.Parameters.Add(Text(write.Language));
            insert.Parameters.Add(Text(write.Digest));
            insert.Parameters.Add(Integer(write.Nodes.Count));
            insert.Parameters.Add(Integer(write.Edges.Count));
            insert.Parameters.Add(Integer(write.FilesIndexed));
            insert.Parameters.Add(Text(write.DiagnosticScope));
            insert.Parameters.Add(Integer(write.ErrorCount));
            insert.Parameters.Add(Nullable(write.SourceRevision));
            insert.Parameters.Add(Timestamp(write.BuiltAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var row in write.Nodes)
        {
            await using var node = connection.CreateCommand();
            node.Transaction = tx;
            node.CommandText = """
                INSERT INTO harness.code_graph_nodes
                (tenant_id,project_id,language,node_id,symbol,kind,file_path,module)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                """;
            node.Parameters.Add(Text(write.TenantId));
            node.Parameters.Add(Text(write.ProjectId));
            node.Parameters.Add(Text(write.Language));
            node.Parameters.Add(Text(row.NodeId));
            node.Parameters.Add(Text(row.Symbol));
            node.Parameters.Add(Integer(row.Kind));
            node.Parameters.Add(Text(row.FilePath));
            node.Parameters.Add(Text(row.Module));
            await node.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var row in write.Edges)
        {
            await using var edge = connection.CreateCommand();
            edge.Transaction = tx;
            edge.CommandText = """
                INSERT INTO harness.code_graph_edges
                (tenant_id,project_id,language,from_node_id,to_node_id,kind)
                VALUES ($1,$2,$3,$4,$5,$6);
                """;
            edge.Parameters.Add(Text(write.TenantId));
            edge.Parameters.Add(Text(write.ProjectId));
            edge.Parameters.Add(Text(write.Language));
            edge.Parameters.Add(Text(row.FromNodeId));
            edge.Parameters.Add(Text(row.ToNodeId));
            edge.Parameters.Add(Integer(row.Kind));
            await edge.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return (await ReadSnapshotAsync(
            connection, null, write.TenantId, write.ProjectId, write.Language, cancellationToken))!;
    }

    public async Task<CodeGraphContent?> LoadAsync(
        string tenantId,
        string projectId,
        string language,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var snapshot = await ReadSnapshotAsync(
            connection, null, tenantId, projectId, language, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var nodes = new List<CodeGraphNodeRow>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT node_id,symbol,kind,file_path,module FROM harness.code_graph_nodes
                WHERE tenant_id=$1 AND project_id=$2 AND language=$3 ORDER BY node_id;
                """;
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Text(projectId));
            query.Parameters.Add(Text(language));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
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
            query.CommandText = """
                SELECT from_node_id,to_node_id,kind FROM harness.code_graph_edges
                WHERE tenant_id=$1 AND project_id=$2 AND language=$3
                ORDER BY from_node_id,to_node_id,kind;
                """;
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Text(projectId));
            query.Parameters.Add(Text(language));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add(new CodeGraphEdgeRow(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        return new CodeGraphContent(snapshot, nodes, edges);
    }

    private static async Task<CodeGraphSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        string tenantId,
        string projectId,
        string language,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText =
            $"{SelectSnapshot} WHERE tenant_id=$1 AND project_id=$2 AND language=$3;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        query.Parameters.Add(Text(language));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CodeGraphSnapshot(
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
                reader.GetFieldValue<DateTimeOffset>(10))
            : null;
    }

    private static NpgsqlParameter Text(string value) =>
        new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Nullable(string? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Integer(int value) =>
        new() { Value = value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter Timestamp(DateTimeOffset value) =>
        new() { Value = value.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz };
}
