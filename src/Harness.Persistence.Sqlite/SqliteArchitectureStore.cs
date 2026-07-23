using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Architecture;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

/// <summary>
/// Persistência SQLite do Architecture Hub. Elementos, relacionamentos, views, metadados de sistema,
/// propostas e histórico append-only. O modelo PROPOSTO e o VIGENTE coexistem separados pela coluna
/// 'state'. Propriedades e coleções são serializadas em JSON; o histórico só recebe INSERT/SELECT.
/// </summary>
public sealed class SqliteArchitectureStore(SqliteWriteDispatcher dispatcher) : IArchitectureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    // Elementos --------------------------------------------------------------------------------------

    public Task CreateElementAsync(ArchitectureElementRecord element, CancellationToken cancellationToken = default)
    {
        ValidateElement(element);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_elements
                    (id,tenant_id,project_id,kind,name,description,properties_json,state,locked,version,
                     proposal_id,counterpart_id,change_kind,created_at,updated_at)
                VALUES ($id,$tenant,$project,$kind,$name,$desc,$props,$state,$locked,$version,
                        $proposal,$counterpart,$change,$created,$updated);
                """;
            BindElement(cmd, element);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task ReplaceElementAsync(ArchitectureElementRecord element, CancellationToken cancellationToken = default)
    {
        ValidateElement(element);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                UPDATE architecture_elements SET
                    project_id=$project,kind=$kind,name=$name,description=$desc,properties_json=$props,
                    state=$state,locked=$locked,version=$version,proposal_id=$proposal,
                    counterpart_id=$counterpart,change_kind=$change,updated_at=$updated
                WHERE tenant_id=$tenant AND id=$id;
                """;
            BindElement(cmd, element);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task DeleteElementAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM architecture_elements WHERE tenant_id=$tenant AND id=$id;";
            Add(cmd, "$tenant", tenantId);
            Add(cmd, "$id", id);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);

    public Task<ArchitectureElementRecord?> GetElementAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = ElementSelect + "WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId);
            Add(q, "$id", id);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadElement(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitectureElementRecord>> ListElementsAsync(
        string tenantId, string? projectId, string state, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = ElementSelect +
                "WHERE tenant_id=$tenant AND state=$state" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (afterId is null ? string.Empty : " AND id>$after") +
                " ORDER BY id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            Add(q, "$state", state);
            if (projectId is not null) Add(q, "$project", projectId);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureElementRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(ReadElement(r));
            return (IReadOnlyList<ArchitectureElementRecord>)results;
        }, cancellationToken);

    // Relacionamentos --------------------------------------------------------------------------------

    public Task CreateRelationshipAsync(ArchitectureRelationshipRecord relationship, CancellationToken cancellationToken = default)
    {
        ValidateRelationship(relationship);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_relationships
                    (id,tenant_id,project_id,source_id,target_id,kind,properties_json,state,version,
                     proposal_id,counterpart_id,change_kind,created_at,updated_at)
                VALUES ($id,$tenant,$project,$source,$target,$kind,$props,$state,$version,
                        $proposal,$counterpart,$change,$created,$updated);
                """;
            BindRelationship(cmd, relationship);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task ReplaceRelationshipAsync(ArchitectureRelationshipRecord relationship, CancellationToken cancellationToken = default)
    {
        ValidateRelationship(relationship);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                UPDATE architecture_relationships SET
                    project_id=$project,source_id=$source,target_id=$target,kind=$kind,
                    properties_json=$props,state=$state,version=$version,proposal_id=$proposal,
                    counterpart_id=$counterpart,change_kind=$change,updated_at=$updated
                WHERE tenant_id=$tenant AND id=$id;
                """;
            BindRelationship(cmd, relationship);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task DeleteRelationshipAsync(string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM architecture_relationships WHERE tenant_id=$tenant AND id=$id;";
            Add(cmd, "$tenant", tenantId);
            Add(cmd, "$id", id);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);

    public Task<ArchitectureRelationshipRecord?> GetRelationshipAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = RelationshipSelect + "WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId);
            Add(q, "$id", id);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadRelationship(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitectureRelationshipRecord>> ListRelationshipsAsync(
        string tenantId, string? projectId, string state, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = RelationshipSelect +
                "WHERE tenant_id=$tenant AND state=$state" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (afterId is null ? string.Empty : " AND id>$after") +
                " ORDER BY id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            Add(q, "$state", state);
            if (projectId is not null) Add(q, "$project", projectId);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureRelationshipRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(ReadRelationship(r));
            return (IReadOnlyList<ArchitectureRelationshipRecord>)results;
        }, cancellationToken);

    // Views ------------------------------------------------------------------------------------------

    public Task CreateViewAsync(ArchitectureViewRecord view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_views
                    (id,tenant_id,project_id,name,description,notation,element_ids_json,
                     relationship_ids_json,filter_kinds_json,filter_tags_json,created_at,updated_at)
                VALUES ($id,$tenant,$project,$name,$desc,$notation,$elements,$relationships,
                        $kinds,$tags,$created,$updated);
                """;
            Add(cmd, "$id", view.Id);
            Add(cmd, "$tenant", view.TenantId);
            Add(cmd, "$project", view.ProjectId);
            Add(cmd, "$name", view.Name);
            Add(cmd, "$desc", view.Description);
            Add(cmd, "$notation", view.Notation);
            Add(cmd, "$elements", Json(view.ElementIds));
            Add(cmd, "$relationships", Json(view.RelationshipIds));
            Add(cmd, "$kinds", Json(view.FilterKinds));
            Add(cmd, "$tags", Json(view.FilterTags));
            Add(cmd, "$created", Store(view.CreatedAt));
            Add(cmd, "$updated", Store(view.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<ArchitectureViewRecord?> GetViewAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = ViewSelect + "WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId);
            Add(q, "$id", id);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadView(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitectureViewRecord>> ListViewsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = ViewSelect +
                "WHERE tenant_id=$tenant" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (afterId is null ? string.Empty : " AND id>$after") +
                " ORDER BY id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            if (projectId is not null) Add(q, "$project", projectId);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureViewRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(ReadView(r));
            return (IReadOnlyList<ArchitectureViewRecord>)results;
        }, cancellationToken);

    // Metadados de sistema ---------------------------------------------------------------------------

    public Task UpsertSystemMetadataAsync(ArchitectureSystemMetadataRecord metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_system_metadata
                    (element_id,tenant_id,project_id,domain,criticality,metadata_json,updated_at)
                VALUES ($element,$tenant,$project,$domain,$criticality,$json,$updated)
                ON CONFLICT(element_id) DO UPDATE SET
                    project_id=excluded.project_id,domain=excluded.domain,criticality=excluded.criticality,
                    metadata_json=excluded.metadata_json,updated_at=excluded.updated_at;
                """;
            Add(cmd, "$element", metadata.ElementId);
            Add(cmd, "$tenant", metadata.TenantId);
            Add(cmd, "$project", metadata.ProjectId);
            Add(cmd, "$domain", metadata.Domain);
            Add(cmd, "$criticality", metadata.Criticality);
            Add(cmd, "$json", JsonSerializer.Serialize(metadata, JsonOptions));
            Add(cmd, "$updated", Store(metadata.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<ArchitectureSystemMetadataRecord?> GetSystemMetadataAsync(
        string tenantId, string elementId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT metadata_json FROM architecture_system_metadata WHERE tenant_id=$tenant AND element_id=$element;";
            Add(q, "$tenant", tenantId);
            Add(q, "$element", elementId);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadMetadata(r.GetString(0)) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitectureSystemMetadataRecord>> ListSystemMetadataAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT metadata_json FROM architecture_system_metadata WHERE tenant_id=$tenant" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (afterId is null ? string.Empty : " AND element_id>$after") +
                " ORDER BY element_id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            if (projectId is not null) Add(q, "$project", projectId);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureSystemMetadataRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(ReadMetadata(r.GetString(0)));
            return (IReadOnlyList<ArchitectureSystemMetadataRecord>)results;
        }, cancellationToken);

    // Propostas --------------------------------------------------------------------------------------

    public Task CreateProposalAsync(ArchitectureProposalRecord proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_proposals
                    (id,tenant_id,project_id,title,status,justification,created_at,applied_at)
                VALUES ($id,$tenant,$project,$title,$status,$justification,$created,$applied);
                """;
            BindProposal(cmd, proposal);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task ReplaceProposalAsync(ArchitectureProposalRecord proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                UPDATE architecture_proposals SET
                    project_id=$project,title=$title,status=$status,justification=$justification,
                    applied_at=$applied
                WHERE tenant_id=$tenant AND id=$id;
                """;
            BindProposal(cmd, proposal);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<ArchitectureProposalRecord?> GetProposalAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = ProposalSelect + "WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId);
            Add(q, "$id", id);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? ReadProposal(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitectureProposalRecord>> ListProposalsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = ProposalSelect +
                "WHERE tenant_id=$tenant" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (afterId is null ? string.Empty : " AND id>$after") +
                " ORDER BY id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            if (projectId is not null) Add(q, "$project", projectId);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureProposalRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(ReadProposal(r));
            return (IReadOnlyList<ArchitectureProposalRecord>)results;
        }, cancellationToken);

    // Histórico --------------------------------------------------------------------------------------

    public Task AppendHistoryAsync(ArchitectureHistoryRecord entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_element_history
                    (id,tenant_id,entity_type,entity_id,version,snapshot_json,change_kind,actor,
                     justification,occurred_at)
                VALUES ($id,$tenant,$type,$entity,$version,$snapshot,$change,$actor,$justification,$at);
                """;
            Add(cmd, "$id", entry.Id);
            Add(cmd, "$tenant", entry.TenantId);
            Add(cmd, "$type", entry.EntityType);
            Add(cmd, "$entity", entry.EntityId);
            Add(cmd, "$version", entry.Version);
            Add(cmd, "$snapshot", entry.SnapshotJson);
            Add(cmd, "$change", entry.ChangeKind);
            Add(cmd, "$actor", entry.Actor);
            Add(cmd, "$justification", entry.Justification);
            Add(cmd, "$at", Store(entry.OccurredAt));
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ArchitectureHistoryRecord>> ListHistoryAsync(
        string tenantId, string entityId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT tenant_id,id,entity_type,entity_id,version,snapshot_json,change_kind,actor," +
                "justification,occurred_at FROM architecture_element_history " +
                "WHERE tenant_id=$tenant AND entity_id=$entity ORDER BY occurred_at DESC, id DESC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            Add(q, "$entity", entityId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureHistoryRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t))
            {
                results.Add(new ArchitectureHistoryRecord(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                    r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetString(8), Parse(r.GetString(9))));
            }

            return (IReadOnlyList<ArchitectureHistoryRecord>)results;
        }, cancellationToken);

    // Descobertas (ARC-06) ---------------------------------------------------------------------------

    public Task CreateDiscoveryAsync(ArchitectureDiscoveryRecord discovery, CancellationToken cancellationToken = default)
    {
        ValidateDiscovery(discovery);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_discoveries
                    (id,tenant_id,project_id,system_id,source_kind,confidence,status,payload_json,created_at,updated_at)
                VALUES ($id,$tenant,$project,$system,$source,$confidence,$status,$json,$created,$updated);
                """;
            BindDiscovery(cmd, discovery);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task ReplaceDiscoveryAsync(ArchitectureDiscoveryRecord discovery, CancellationToken cancellationToken = default)
    {
        ValidateDiscovery(discovery);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                UPDATE architecture_discoveries SET
                    project_id=$project,system_id=$system,source_kind=$source,confidence=$confidence,
                    status=$status,payload_json=$json,updated_at=$updated
                WHERE tenant_id=$tenant AND id=$id;
                """;
            BindDiscovery(cmd, discovery);
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<ArchitectureDiscoveryRecord?> GetDiscoveryAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT payload_json FROM architecture_discoveries WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId);
            Add(q, "$id", id);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? Read<ArchitectureDiscoveryRecord>(r.GetString(0)) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitectureDiscoveryRecord>> ListDiscoveriesAsync(
        string tenantId, string? projectId, string? systemId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT payload_json FROM architecture_discoveries WHERE tenant_id=$tenant" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (systemId is null ? string.Empty : " AND system_id=$system") +
                (afterId is null ? string.Empty : " AND id>$after") +
                " ORDER BY id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            if (projectId is not null) Add(q, "$project", projectId);
            if (systemId is not null) Add(q, "$system", systemId);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureDiscoveryRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(Read<ArchitectureDiscoveryRecord>(r.GetString(0)));
            return (IReadOnlyList<ArchitectureDiscoveryRecord>)results;
        }, cancellationToken);

    // Padrões & Decisões (ARC-08) --------------------------------------------------------------------

    public Task UpsertPatternAsync(ArchitecturePatternRecord pattern, CancellationToken cancellationToken = default)
    {
        ValidatePattern(pattern);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_patterns
                    (id,tenant_id,project_id,kind,status,payload_json,created_at,updated_at)
                VALUES ($id,$tenant,$project,$kind,$status,$json,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET
                    project_id=excluded.project_id,kind=excluded.kind,status=excluded.status,
                    payload_json=excluded.payload_json,updated_at=excluded.updated_at;
                """;
            Add(cmd, "$id", pattern.Id);
            Add(cmd, "$tenant", pattern.TenantId);
            Add(cmd, "$project", pattern.ProjectId);
            Add(cmd, "$kind", pattern.Kind);
            Add(cmd, "$status", pattern.Status);
            Add(cmd, "$json", Json(pattern));
            Add(cmd, "$created", Store(pattern.CreatedAt));
            Add(cmd, "$updated", Store(pattern.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<ArchitecturePatternRecord?> GetPatternAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT payload_json FROM architecture_patterns WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId);
            Add(q, "$id", id);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? Read<ArchitecturePatternRecord>(r.GetString(0)) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitecturePatternRecord>> ListPatternsAsync(
        string tenantId, string? projectId, string? kind, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT payload_json FROM architecture_patterns WHERE tenant_id=$tenant" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (kind is null ? string.Empty : " AND kind=$kind") +
                (afterId is null ? string.Empty : " AND id>$after") +
                " ORDER BY id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            if (projectId is not null) Add(q, "$project", projectId);
            if (kind is not null) Add(q, "$kind", kind);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitecturePatternRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(Read<ArchitecturePatternRecord>(r.GetString(0)));
            return (IReadOnlyList<ArchitecturePatternRecord>)results;
        }, cancellationToken);

    // Baselines de entrega (ARC-10) ------------------------------------------------------------------

    public Task UpsertBaselineAsync(ArchitectureBaselineRecord baseline, CancellationToken cancellationToken = default)
    {
        ValidateBaseline(baseline);
        return _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO architecture_baselines
                    (id,tenant_id,project_id,status,payload_json,created_at,updated_at)
                VALUES ($id,$tenant,$project,$status,$json,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET
                    project_id=excluded.project_id,status=excluded.status,payload_json=excluded.payload_json,
                    updated_at=excluded.updated_at;
                """;
            Add(cmd, "$id", baseline.Id);
            Add(cmd, "$tenant", baseline.TenantId);
            Add(cmd, "$project", baseline.ProjectId);
            Add(cmd, "$status", baseline.Status);
            Add(cmd, "$json", Json(baseline));
            Add(cmd, "$created", Store(baseline.CreatedAt));
            Add(cmd, "$updated", Store(baseline.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(t);
            return true;
        }, cancellationToken);
    }

    public Task<ArchitectureBaselineRecord?> GetBaselineAsync(
        string tenantId, string id, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT payload_json FROM architecture_baselines WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenantId);
            Add(q, "$id", id);
            await using var r = await q.ExecuteReaderAsync(t);
            return await r.ReadAsync(t) ? Read<ArchitectureBaselineRecord>(r.GetString(0)) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ArchitectureBaselineRecord>> ListBaselinesAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, t) =>
        {
            await using var q = c.CreateCommand();
            q.CommandText =
                "SELECT payload_json FROM architecture_baselines WHERE tenant_id=$tenant" +
                (projectId is null ? string.Empty : " AND project_id=$project") +
                (afterId is null ? string.Empty : " AND id>$after") +
                " ORDER BY id ASC LIMIT $limit;";
            Add(q, "$tenant", tenantId);
            if (projectId is not null) Add(q, "$project", projectId);
            if (afterId is not null) Add(q, "$after", afterId);
            Add(q, "$limit", Math.Clamp(limit, 1, 500));
            var results = new List<ArchitectureBaselineRecord>();
            await using var r = await q.ExecuteReaderAsync(t);
            while (await r.ReadAsync(t)) results.Add(Read<ArchitectureBaselineRecord>(r.GetString(0)));
            return (IReadOnlyList<ArchitectureBaselineRecord>)results;
        }, cancellationToken);

    // Binding / leitura ------------------------------------------------------------------------------

    private static void BindDiscovery(SqliteCommand cmd, ArchitectureDiscoveryRecord d)
    {
        Add(cmd, "$id", d.Id);
        Add(cmd, "$tenant", d.TenantId);
        Add(cmd, "$project", d.ProjectId);
        Add(cmd, "$system", d.SystemId);
        Add(cmd, "$source", d.SourceKind);
        Add(cmd, "$confidence", d.Confidence);
        Add(cmd, "$status", d.Status);
        Add(cmd, "$json", Json(d));
        Add(cmd, "$created", Store(d.CreatedAt));
        Add(cmd, "$updated", Store(d.UpdatedAt));
    }

    private static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Architecture {typeof(T).Name} could not be read back.");

    private static void ValidateDiscovery(ArchitectureDiscoveryRecord discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentException.ThrowIfNullOrWhiteSpace(discovery.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(discovery.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(discovery.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(discovery.Confidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(discovery.Status);
        ArgumentNullException.ThrowIfNull(discovery.PendingQuestions);
    }

    private static void ValidatePattern(ArchitecturePatternRecord pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern.Status);
        ArgumentNullException.ThrowIfNull(pattern.Tags);
    }

    private static void ValidateBaseline(ArchitectureBaselineRecord baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline.Status);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline.BaselineSnapshotJson);
    }

    private const string ElementSelect =
        "SELECT tenant_id,id,project_id,kind,name,description,properties_json,state,locked,version," +
        "proposal_id,counterpart_id,change_kind,created_at,updated_at FROM architecture_elements ";

    private const string RelationshipSelect =
        "SELECT tenant_id,id,project_id,source_id,target_id,kind,properties_json,state,version," +
        "proposal_id,counterpart_id,change_kind,created_at,updated_at FROM architecture_relationships ";

    private const string ViewSelect =
        "SELECT tenant_id,id,project_id,name,description,notation,element_ids_json," +
        "relationship_ids_json,filter_kinds_json,filter_tags_json,created_at,updated_at FROM architecture_views ";

    private const string ProposalSelect =
        "SELECT tenant_id,id,project_id,title,status,justification,created_at,applied_at FROM architecture_proposals ";

    private static void BindElement(SqliteCommand cmd, ArchitectureElementRecord e)
    {
        Add(cmd, "$id", e.Id);
        Add(cmd, "$tenant", e.TenantId);
        Add(cmd, "$project", e.ProjectId);
        Add(cmd, "$kind", e.Kind);
        Add(cmd, "$name", e.Name);
        Add(cmd, "$desc", e.Description);
        Add(cmd, "$props", Json(e.Properties));
        Add(cmd, "$state", e.State);
        Add(cmd, "$locked", e.Locked ? 1 : 0);
        Add(cmd, "$version", e.Version);
        Add(cmd, "$proposal", e.ProposalId);
        Add(cmd, "$counterpart", e.CounterpartId);
        Add(cmd, "$change", e.ChangeKind);
        Add(cmd, "$created", Store(e.CreatedAt));
        Add(cmd, "$updated", Store(e.UpdatedAt));
    }

    private static void BindRelationship(SqliteCommand cmd, ArchitectureRelationshipRecord rel)
    {
        Add(cmd, "$id", rel.Id);
        Add(cmd, "$tenant", rel.TenantId);
        Add(cmd, "$project", rel.ProjectId);
        Add(cmd, "$source", rel.SourceId);
        Add(cmd, "$target", rel.TargetId);
        Add(cmd, "$kind", rel.Kind);
        Add(cmd, "$props", Json(rel.Properties));
        Add(cmd, "$state", rel.State);
        Add(cmd, "$version", rel.Version);
        Add(cmd, "$proposal", rel.ProposalId);
        Add(cmd, "$counterpart", rel.CounterpartId);
        Add(cmd, "$change", rel.ChangeKind);
        Add(cmd, "$created", Store(rel.CreatedAt));
        Add(cmd, "$updated", Store(rel.UpdatedAt));
    }

    private static void BindProposal(SqliteCommand cmd, ArchitectureProposalRecord p)
    {
        Add(cmd, "$id", p.Id);
        Add(cmd, "$tenant", p.TenantId);
        Add(cmd, "$project", p.ProjectId);
        Add(cmd, "$title", p.Title);
        Add(cmd, "$status", p.Status);
        Add(cmd, "$justification", p.Justification);
        Add(cmd, "$created", Store(p.CreatedAt));
        Add(cmd, "$applied", p.AppliedAt.HasValue ? Store(p.AppliedAt.Value) : null);
    }

    private static ArchitectureElementRecord ReadElement(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetString(5), ReadProperties(r.GetString(6)), r.GetString(7),
        r.GetInt32(8) == 1, r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
        Parse(r.GetString(13)), Parse(r.GetString(14)));

    private static ArchitectureRelationshipRecord ReadRelationship(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetString(5), ReadProperties(r.GetString(6)), r.GetString(7), r.GetInt32(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11), Parse(r.GetString(12)), Parse(r.GetString(13)));

    private static ArchitectureViewRecord ReadView(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetString(5), ReadList(r.GetString(6)), ReadList(r.GetString(7)),
        ReadList(r.GetString(8)), ReadList(r.GetString(9)), Parse(r.GetString(10)), Parse(r.GetString(11)));

    private static ArchitectureProposalRecord ReadProposal(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), Parse(r.GetString(6)),
        r.IsDBNull(7) ? null : Parse(r.GetString(7)));

    private static ArchitectureSystemMetadataRecord ReadMetadata(string json) =>
        JsonSerializer.Deserialize<ArchitectureSystemMetadataRecord>(json, JsonOptions)
            ?? throw new InvalidOperationException("Architecture system metadata could not be read back.");

    private static Dictionary<string, string> ReadProperties(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? new Dictionary<string, string>();

    private static List<string> ReadList(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static void ValidateElement(ArchitectureElementRecord element)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.State);
        ArgumentNullException.ThrowIfNull(element.Properties);
    }

    private static void ValidateRelationship(ArchitectureRelationshipRecord relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.TargetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.Kind);
        ArgumentNullException.ThrowIfNull(relationship.Properties);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
