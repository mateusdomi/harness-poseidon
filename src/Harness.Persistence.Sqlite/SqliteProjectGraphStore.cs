using System.Globalization;
using Harness.Persistence.Abstractions.Graph;
using Harness.SharedKernel.Graph;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteProjectGraphStore(SqliteWriteDispatcher dispatcher) : IProjectGraphStore
{
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ProjectGraphStoreSnapshot> GetAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            var nodes = new List<GraphNode>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText =
                    "SELECT id,project_id,type,canonical_source_id,canonical_source_kind,version," +
                    "state,provenance,confidence,title,stale_cause_node_id,stale_cause_version," +
                    "created_at,updated_at FROM project_graph_nodes " +
                    "WHERE tenant_id=$tenant AND project_id=$project ORDER BY id;";
                query.Parameters.AddWithValue("$tenant", tenantId);
                query.Parameters.AddWithValue("$project", projectId);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    nodes.Add(new GraphNode(
                        reader.GetString(0),
                        reader.GetString(1),
                        GraphStorageNames.NodeType(reader.GetString(2)),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt32(5),
                        GraphStorageNames.NodeState(reader.GetString(6)),
                        GraphStorageNames.Provenance(reader.GetString(7)),
                        reader.GetDouble(8),
                        reader.GetString(9),
                        ParseAt(reader.GetString(12)),
                        ParseAt(reader.GetString(13)),
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetInt32(11)));
                }
            }

            var edges = new List<GraphEdge>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText =
                    "SELECT id,from_node_id,to_node_id,relation_type,provenance,confidence,status," +
                    "valid_from,valid_until FROM project_graph_edges " +
                    "WHERE tenant_id=$tenant AND project_id=$project ORDER BY id;";
                query.Parameters.AddWithValue("$tenant", tenantId);
                query.Parameters.AddWithValue("$project", projectId);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    edges.Add(new GraphEdge(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        GraphStorageNames.RelationType(reader.GetString(3)),
                        GraphStorageNames.Provenance(reader.GetString(4)),
                        reader.GetDouble(5),
                        GraphStorageNames.EdgeStatus(reader.GetString(6)),
                        ParseAt(reader.GetString(7)),
                        reader.IsDBNull(8) ? null : ParseAt(reader.GetString(8))));
                }
            }

            var version = 0L;
            await using (var query = connection.CreateCommand())
            {
                query.CommandText =
                    "SELECT COALESCE(MAX(graph_version),0) FROM project_graph_snapshots " +
                    "WHERE tenant_id=$tenant AND project_id=$project;";
                query.Parameters.AddWithValue("$tenant", tenantId);
                query.Parameters.AddWithValue("$project", projectId);
                version = Convert.ToInt64(
                    await query.ExecuteScalarAsync(token) ?? 0L, CultureInfo.InvariantCulture);
            }

            return new ProjectGraphStoreSnapshot(version, nodes, edges);
        }, cancellationToken);
    }

    public Task<long> ApplyAsync(
        ProjectGraphApplyCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(token);

            // Estado atual mínimo para o diff: versão da fonte e estado operacional por nó.
            var existing = new Dictionary<string, (int Version, string State)>(StringComparer.Ordinal);
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText =
                    "SELECT id,version,state FROM project_graph_nodes " +
                    "WHERE tenant_id=$tenant AND project_id=$project;";
                query.Parameters.AddWithValue("$tenant", command.TenantId);
                query.Parameters.AddWithValue("$project", command.ProjectId);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    existing[reader.GetString(0)] = (reader.GetInt32(1), reader.GetString(2));
                }
            }

            var desiredNodeIds = command.Nodes.Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);

            // Arestas primeiro (FK aponta para nós): remove todas e reescreve as desejadas.
            // O conjunto de arestas é função pura das fontes — não carrega estado operacional
            // próprio, então reescrever é a forma mais simples de garantir equivalência.
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    "DELETE FROM project_graph_edges WHERE tenant_id=$tenant AND project_id=$project;";
                delete.Parameters.AddWithValue("$tenant", command.TenantId);
                delete.Parameters.AddWithValue("$project", command.ProjectId);
                await delete.ExecuteNonQueryAsync(token);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    "DELETE FROM project_graph_nodes " +
                    "WHERE tenant_id=$tenant AND project_id=$project AND id NOT IN " +
                    $"({string.Join(",", desiredNodeIds.Select((_, index) => $"$node{index}"))});";
                delete.Parameters.AddWithValue("$tenant", command.TenantId);
                delete.Parameters.AddWithValue("$project", command.ProjectId);
                var position = 0;
                foreach (var id in desiredNodeIds)
                {
                    delete.Parameters.AddWithValue($"$node{position++}", id);
                }

                if (desiredNodeIds.Count > 0)
                {
                    await delete.ExecuteNonQueryAsync(token);
                }
            }

            if (desiredNodeIds.Count == 0)
            {
                await using var wipe = connection.CreateCommand();
                wipe.Transaction = transaction;
                wipe.CommandText =
                    "DELETE FROM project_graph_nodes WHERE tenant_id=$tenant AND project_id=$project;";
                wipe.Parameters.AddWithValue("$tenant", command.TenantId);
                wipe.Parameters.AddWithValue("$project", command.ProjectId);
                await wipe.ExecuteNonQueryAsync(token);
            }

            foreach (var node in command.Nodes)
            {
                // STALE é operacional: se a fonte NÃO avançou de versão, o apply preserva a
                // marca (e a causa); se avançou, a própria mudança era a revalidação esperada.
                var keepStale = existing.TryGetValue(node.Id, out var current) &&
                    current.State == "stale" && current.Version == node.Version &&
                    node.State == GraphNodeState.Active;

                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText =
                    """
                    INSERT INTO project_graph_nodes
                        (tenant_id,id,project_id,type,canonical_source_id,canonical_source_kind,
                         version,state,provenance,confidence,title,stale_cause_node_id,
                         stale_cause_version,created_at,updated_at)
                    VALUES ($tenant,$id,$project,$type,$sourceId,$sourceKind,$version,$state,
                            $provenance,$confidence,$title,NULL,NULL,$at,$at)
                    ON CONFLICT(tenant_id,project_id,id) DO UPDATE SET
                        version=excluded.version,
                        state=CASE WHEN $keepStale THEN project_graph_nodes.state ELSE excluded.state END,
                        stale_cause_node_id=CASE WHEN $keepStale
                            THEN project_graph_nodes.stale_cause_node_id ELSE NULL END,
                        stale_cause_version=CASE WHEN $keepStale
                            THEN project_graph_nodes.stale_cause_version ELSE NULL END,
                        canonical_source_kind=excluded.canonical_source_kind,
                        provenance=excluded.provenance,
                        confidence=excluded.confidence,
                        title=excluded.title,
                        updated_at=excluded.updated_at;
                    """;
                upsert.Parameters.AddWithValue("$tenant", command.TenantId);
                upsert.Parameters.AddWithValue("$id", node.Id);
                upsert.Parameters.AddWithValue("$project", command.ProjectId);
                upsert.Parameters.AddWithValue("$type", GraphStorageNames.Of(node.Type));
                upsert.Parameters.AddWithValue("$sourceId", node.CanonicalSourceId);
                upsert.Parameters.AddWithValue("$sourceKind", node.CanonicalSourceKind);
                upsert.Parameters.AddWithValue("$version", node.Version);
                upsert.Parameters.AddWithValue("$state", GraphStorageNames.Of(node.State));
                upsert.Parameters.AddWithValue("$provenance", GraphStorageNames.Of(node.Provenance));
                upsert.Parameters.AddWithValue("$confidence", node.Confidence);
                upsert.Parameters.AddWithValue("$title", node.Title);
                upsert.Parameters.AddWithValue("$keepStale", keepStale ? 1 : 0);
                upsert.Parameters.AddWithValue(
                    "$at", command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                await upsert.ExecuteNonQueryAsync(token);
            }

            foreach (var edge in command.Edges)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO project_graph_edges
                        (tenant_id,id,project_id,from_node_id,to_node_id,relation_type,
                         provenance,confidence,status,valid_from,valid_until)
                    VALUES ($tenant,$id,$project,$from,$to,$relation,$provenance,$confidence,
                            $status,$validFrom,$validUntil);
                    """;
                insert.Parameters.AddWithValue("$tenant", command.TenantId);
                insert.Parameters.AddWithValue("$id", edge.Id);
                insert.Parameters.AddWithValue("$project", command.ProjectId);
                insert.Parameters.AddWithValue("$from", edge.FromNodeId);
                insert.Parameters.AddWithValue("$to", edge.ToNodeId);
                insert.Parameters.AddWithValue("$relation", GraphStorageNames.Of(edge.RelationType));
                insert.Parameters.AddWithValue("$provenance", GraphStorageNames.Of(edge.Provenance));
                insert.Parameters.AddWithValue("$confidence", edge.Confidence);
                insert.Parameters.AddWithValue("$status", GraphStorageNames.Of(edge.Status));
                insert.Parameters.AddWithValue(
                    "$validFrom",
                    edge.ValidFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue(
                    "$validUntil",
                    edge.ValidUntil is { } until
                        ? until.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                        : DBNull.Value);
                await insert.ExecuteNonQueryAsync(token);
            }

            long version;
            await using (var next = connection.CreateCommand())
            {
                next.Transaction = transaction;
                next.CommandText =
                    "SELECT COALESCE(MAX(graph_version),0)+1 FROM project_graph_snapshots " +
                    "WHERE tenant_id=$tenant AND project_id=$project;";
                next.Parameters.AddWithValue("$tenant", command.TenantId);
                next.Parameters.AddWithValue("$project", command.ProjectId);
                version = Convert.ToInt64(
                    await next.ExecuteScalarAsync(token) ?? 1L, CultureInfo.InvariantCulture);
            }

            await using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = transaction;
                snapshot.CommandText =
                    """
                    INSERT INTO project_graph_snapshots
                        (tenant_id,project_id,graph_version,reason,node_count,edge_count,created_at)
                    VALUES ($tenant,$project,$version,$reason,$nodes,$edges,$at);
                    """;
                snapshot.Parameters.AddWithValue("$tenant", command.TenantId);
                snapshot.Parameters.AddWithValue("$project", command.ProjectId);
                snapshot.Parameters.AddWithValue("$version", version);
                snapshot.Parameters.AddWithValue("$reason", command.Reason);
                snapshot.Parameters.AddWithValue("$nodes", command.Nodes.Count);
                snapshot.Parameters.AddWithValue("$edges", command.Edges.Count);
                snapshot.Parameters.AddWithValue(
                    "$at", command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                await snapshot.ExecuteNonQueryAsync(token);
            }

            await transaction.CommitAsync(token);
            return version;
        }, cancellationToken);
    }

    public Task MarkStaleAsync(
        ProjectGraphStaleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            foreach (var mark in command.Marks)
            {
                await using var update = connection.CreateCommand();
                update.CommandText =
                    """
                    UPDATE project_graph_nodes
                    SET state='stale', stale_cause_node_id=$cause, stale_cause_version=$causeVersion,
                        updated_at=$at
                    WHERE tenant_id=$tenant AND project_id=$project AND id=$id AND state!='retired';
                    """;
                update.Parameters.AddWithValue("$tenant", command.TenantId);
                update.Parameters.AddWithValue("$project", command.ProjectId);
                update.Parameters.AddWithValue("$id", mark.NodeId);
                update.Parameters.AddWithValue("$cause", mark.CauseNodeId);
                update.Parameters.AddWithValue("$causeVersion", mark.CauseVersion);
                update.Parameters.AddWithValue(
                    "$at", command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                await update.ExecuteNonQueryAsync(token);
            }

            return 0;
        }, cancellationToken);
    }

    public Task ClearStaleAsync(
        string tenantId, string projectId, string nodeId, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE project_graph_nodes
                SET state='active', stale_cause_node_id=NULL, stale_cause_version=NULL, updated_at=$at
                WHERE tenant_id=$tenant AND project_id=$project AND id=$id AND state='stale';
                """;
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$project", projectId);
            update.Parameters.AddWithValue("$id", nodeId);
            update.Parameters.AddWithValue(
                "$at", occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await update.ExecuteNonQueryAsync(token);
            return 0;
        }, cancellationToken);
    }

    private static DateTimeOffset ParseAt(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
