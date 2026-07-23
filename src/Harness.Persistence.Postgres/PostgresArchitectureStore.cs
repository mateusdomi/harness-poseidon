using System.Text.Json;
using Harness.Persistence.Abstractions.Architecture;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

/// <summary>
/// Persistência PostgreSQL do Architecture Hub (schema harness). Espelha o store SQLite: elementos,
/// relacionamentos, views, metadados de sistema, propostas e histórico append-only. O modelo PROPOSTO
/// e o VIGENTE coexistem separados pela coluna 'state'. Coleções vivem em jsonb.
/// </summary>
public sealed class PostgresArchitectureStore(NpgsqlDataSource dataSource) : IArchitectureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    // Elementos --------------------------------------------------------------------------------------

    public async Task CreateElementAsync(ArchitectureElementRecord element, CancellationToken cancellationToken = default)
    {
        ValidateElement(element);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO harness.architecture_elements
                (id,tenant_id,project_id,kind,name,description,properties_json,state,locked,version,
                 proposal_id,counterpart_id,change_kind,created_at,updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15);
            """;
        BindElement(cmd, element);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceElementAsync(ArchitectureElementRecord element, CancellationToken cancellationToken = default)
    {
        ValidateElement(element);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            UPDATE harness.architecture_elements SET
                project_id=$3,kind=$4,name=$5,description=$6,properties_json=$7,state=$8,locked=$9,
                version=$10,proposal_id=$11,counterpart_id=$12,change_kind=$13,updated_at=$15
            WHERE tenant_id=$2 AND id=$1;
            """;
        BindElement(cmd, element);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteElementAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM harness.architecture_elements WHERE tenant_id=$1 AND id=$2;";
        cmd.Parameters.Add(Text(tenantId));
        cmd.Parameters.Add(Text(id));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArchitectureElementRecord?> GetElementAsync(
        string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = ElementSelect + "WHERE tenant_id=$1 AND id=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(id));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadElement(r) : null;
    }

    public async Task<IReadOnlyList<ArchitectureElementRecord>> ListElementsAsync(
        string tenantId, string? projectId, string state, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = ElementSelect +
            "WHERE tenant_id=$1 AND state=$2" +
            (projectId is null ? string.Empty : " AND project_id=$3") +
            (afterId is null ? string.Empty : $" AND id>${(projectId is null ? 3 : 4)}") +
            " ORDER BY id ASC LIMIT $" + (2 + (projectId is null ? 0 : 1) + (afterId is null ? 0 : 1) + 1) + ";";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(state));
        if (projectId is not null) q.Parameters.Add(Text(projectId));
        if (afterId is not null) q.Parameters.Add(Text(afterId));
        q.Parameters.Add(Int(Math.Clamp(limit, 1, 500)));
        var results = new List<ArchitectureElementRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) results.Add(ReadElement(r));
        return results;
    }

    // Relacionamentos --------------------------------------------------------------------------------

    public async Task CreateRelationshipAsync(ArchitectureRelationshipRecord relationship, CancellationToken cancellationToken = default)
    {
        ValidateRelationship(relationship);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO harness.architecture_relationships
                (id,tenant_id,project_id,source_id,target_id,kind,properties_json,state,version,
                 proposal_id,counterpart_id,change_kind,created_at,updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14);
            """;
        BindRelationship(cmd, relationship);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceRelationshipAsync(ArchitectureRelationshipRecord relationship, CancellationToken cancellationToken = default)
    {
        ValidateRelationship(relationship);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            UPDATE harness.architecture_relationships SET
                project_id=$3,source_id=$4,target_id=$5,kind=$6,properties_json=$7,state=$8,version=$9,
                proposal_id=$10,counterpart_id=$11,change_kind=$12,updated_at=$14
            WHERE tenant_id=$2 AND id=$1;
            """;
        BindRelationship(cmd, relationship);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteRelationshipAsync(string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM harness.architecture_relationships WHERE tenant_id=$1 AND id=$2;";
        cmd.Parameters.Add(Text(tenantId));
        cmd.Parameters.Add(Text(id));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArchitectureRelationshipRecord?> GetRelationshipAsync(
        string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = RelationshipSelect + "WHERE tenant_id=$1 AND id=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(id));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadRelationship(r) : null;
    }

    public async Task<IReadOnlyList<ArchitectureRelationshipRecord>> ListRelationshipsAsync(
        string tenantId, string? projectId, string state, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = RelationshipSelect +
            "WHERE tenant_id=$1 AND state=$2" +
            (projectId is null ? string.Empty : " AND project_id=$3") +
            (afterId is null ? string.Empty : $" AND id>${(projectId is null ? 3 : 4)}") +
            " ORDER BY id ASC LIMIT $" + (2 + (projectId is null ? 0 : 1) + (afterId is null ? 0 : 1) + 1) + ";";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(state));
        if (projectId is not null) q.Parameters.Add(Text(projectId));
        if (afterId is not null) q.Parameters.Add(Text(afterId));
        q.Parameters.Add(Int(Math.Clamp(limit, 1, 500)));
        var results = new List<ArchitectureRelationshipRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) results.Add(ReadRelationship(r));
        return results;
    }

    // Views ------------------------------------------------------------------------------------------

    public async Task CreateViewAsync(ArchitectureViewRecord view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO harness.architecture_views
                (id,tenant_id,project_id,name,description,notation,element_ids_json,
                 relationship_ids_json,filter_kinds_json,filter_tags_json,created_at,updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12);
            """;
        cmd.Parameters.Add(Text(view.Id));
        cmd.Parameters.Add(Text(view.TenantId));
        cmd.Parameters.Add(NullableText(view.ProjectId));
        cmd.Parameters.Add(Text(view.Name));
        cmd.Parameters.Add(Text(view.Description));
        cmd.Parameters.Add(Text(view.Notation));
        cmd.Parameters.Add(Jsonb(Json(view.ElementIds)));
        cmd.Parameters.Add(Jsonb(Json(view.RelationshipIds)));
        cmd.Parameters.Add(Jsonb(Json(view.FilterKinds)));
        cmd.Parameters.Add(Jsonb(Json(view.FilterTags)));
        cmd.Parameters.Add(Timestamp(view.CreatedAt));
        cmd.Parameters.Add(Timestamp(view.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArchitectureViewRecord?> GetViewAsync(
        string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = ViewSelect + "WHERE tenant_id=$1 AND id=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(id));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadView(r) : null;
    }

    public async Task<IReadOnlyList<ArchitectureViewRecord>> ListViewsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = ViewSelect +
            "WHERE tenant_id=$1" +
            (projectId is null ? string.Empty : " AND project_id=$2") +
            (afterId is null ? string.Empty : $" AND id>${(projectId is null ? 2 : 3)}") +
            " ORDER BY id ASC LIMIT $" + (1 + (projectId is null ? 0 : 1) + (afterId is null ? 0 : 1) + 1) + ";";
        q.Parameters.Add(Text(tenantId));
        if (projectId is not null) q.Parameters.Add(Text(projectId));
        if (afterId is not null) q.Parameters.Add(Text(afterId));
        q.Parameters.Add(Int(Math.Clamp(limit, 1, 500)));
        var results = new List<ArchitectureViewRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) results.Add(ReadView(r));
        return results;
    }

    // Metadados de sistema ---------------------------------------------------------------------------

    public async Task UpsertSystemMetadataAsync(ArchitectureSystemMetadataRecord metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO harness.architecture_system_metadata
                (element_id,tenant_id,project_id,domain,criticality,metadata_json,updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            ON CONFLICT(element_id) DO UPDATE SET
                project_id=excluded.project_id,domain=excluded.domain,criticality=excluded.criticality,
                metadata_json=excluded.metadata_json,updated_at=excluded.updated_at;
            """;
        cmd.Parameters.Add(Text(metadata.ElementId));
        cmd.Parameters.Add(Text(metadata.TenantId));
        cmd.Parameters.Add(NullableText(metadata.ProjectId));
        cmd.Parameters.Add(NullableText(metadata.Domain));
        cmd.Parameters.Add(Text(metadata.Criticality));
        cmd.Parameters.Add(Jsonb(JsonSerializer.Serialize(metadata, JsonOptions)));
        cmd.Parameters.Add(Timestamp(metadata.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArchitectureSystemMetadataRecord?> GetSystemMetadataAsync(
        string tenantId, string elementId, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText =
            "SELECT metadata_json FROM harness.architecture_system_metadata WHERE tenant_id=$1 AND element_id=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(elementId));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadMetadata(r.GetString(0)) : null;
    }

    public async Task<IReadOnlyList<ArchitectureSystemMetadataRecord>> ListSystemMetadataAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText =
            "SELECT metadata_json FROM harness.architecture_system_metadata WHERE tenant_id=$1" +
            (projectId is null ? string.Empty : " AND project_id=$2") +
            (afterId is null ? string.Empty : $" AND element_id>${(projectId is null ? 2 : 3)}") +
            " ORDER BY element_id ASC LIMIT $" + (1 + (projectId is null ? 0 : 1) + (afterId is null ? 0 : 1) + 1) + ";";
        q.Parameters.Add(Text(tenantId));
        if (projectId is not null) q.Parameters.Add(Text(projectId));
        if (afterId is not null) q.Parameters.Add(Text(afterId));
        q.Parameters.Add(Int(Math.Clamp(limit, 1, 500)));
        var results = new List<ArchitectureSystemMetadataRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) results.Add(ReadMetadata(r.GetString(0)));
        return results;
    }

    // Propostas --------------------------------------------------------------------------------------

    public async Task CreateProposalAsync(ArchitectureProposalRecord proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO harness.architecture_proposals
                (id,tenant_id,project_id,title,status,justification,created_at,applied_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
            """;
        BindProposal(cmd, proposal);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceProposalAsync(ArchitectureProposalRecord proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            UPDATE harness.architecture_proposals SET
                project_id=$3,title=$4,status=$5,justification=$6,applied_at=$8
            WHERE tenant_id=$2 AND id=$1;
            """;
        BindProposal(cmd, proposal);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArchitectureProposalRecord?> GetProposalAsync(
        string tenantId, string id, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = ProposalSelect + "WHERE tenant_id=$1 AND id=$2;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(id));
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadProposal(r) : null;
    }

    public async Task<IReadOnlyList<ArchitectureProposalRecord>> ListProposalsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = ProposalSelect +
            "WHERE tenant_id=$1" +
            (projectId is null ? string.Empty : " AND project_id=$2") +
            (afterId is null ? string.Empty : $" AND id>${(projectId is null ? 2 : 3)}") +
            " ORDER BY id ASC LIMIT $" + (1 + (projectId is null ? 0 : 1) + (afterId is null ? 0 : 1) + 1) + ";";
        q.Parameters.Add(Text(tenantId));
        if (projectId is not null) q.Parameters.Add(Text(projectId));
        if (afterId is not null) q.Parameters.Add(Text(afterId));
        q.Parameters.Add(Int(Math.Clamp(limit, 1, 500)));
        var results = new List<ArchitectureProposalRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) results.Add(ReadProposal(r));
        return results;
    }

    // Histórico --------------------------------------------------------------------------------------

    public async Task AppendHistoryAsync(ArchitectureHistoryRecord entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO harness.architecture_element_history
                (id,tenant_id,entity_type,entity_id,version,snapshot_json,change_kind,actor,
                 justification,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10);
            """;
        cmd.Parameters.Add(Text(entry.Id));
        cmd.Parameters.Add(Text(entry.TenantId));
        cmd.Parameters.Add(Text(entry.EntityType));
        cmd.Parameters.Add(Text(entry.EntityId));
        cmd.Parameters.Add(Int(entry.Version));
        cmd.Parameters.Add(Jsonb(entry.SnapshotJson));
        cmd.Parameters.Add(Text(entry.ChangeKind));
        cmd.Parameters.Add(NullableText(entry.Actor));
        cmd.Parameters.Add(NullableText(entry.Justification));
        cmd.Parameters.Add(Timestamp(entry.OccurredAt));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArchitectureHistoryRecord>> ListHistoryAsync(
        string tenantId, string entityId, int limit, CancellationToken cancellationToken = default)
    {
        await using var c = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText =
            "SELECT tenant_id,id,entity_type,entity_id,version,snapshot_json,change_kind,actor," +
            "justification,occurred_at FROM harness.architecture_element_history " +
            "WHERE tenant_id=$1 AND entity_id=$2 ORDER BY occurred_at DESC, id DESC LIMIT $3;";
        q.Parameters.Add(Text(tenantId));
        q.Parameters.Add(Text(entityId));
        q.Parameters.Add(Int(Math.Clamp(limit, 1, 500)));
        var results = new List<ArchitectureHistoryRecord>();
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            results.Add(new ArchitectureHistoryRecord(
                r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), r.GetString(2), r.GetString(3).TrimEnd(),
                r.GetInt32(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.GetFieldValue<DateTimeOffset>(9)));
        }

        return results;
    }

    // Binding / leitura ------------------------------------------------------------------------------

    private const string ElementSelect =
        "SELECT tenant_id,id,project_id,kind,name,description,properties_json,state,locked,version," +
        "proposal_id,counterpart_id,change_kind,created_at,updated_at FROM harness.architecture_elements ";

    private const string RelationshipSelect =
        "SELECT tenant_id,id,project_id,source_id,target_id,kind,properties_json,state,version," +
        "proposal_id,counterpart_id,change_kind,created_at,updated_at FROM harness.architecture_relationships ";

    private const string ViewSelect =
        "SELECT tenant_id,id,project_id,name,description,notation,element_ids_json," +
        "relationship_ids_json,filter_kinds_json,filter_tags_json,created_at,updated_at FROM harness.architecture_views ";

    private const string ProposalSelect =
        "SELECT tenant_id,id,project_id,title,status,justification,created_at,applied_at FROM harness.architecture_proposals ";

    private static void BindElement(NpgsqlCommand cmd, ArchitectureElementRecord e)
    {
        cmd.Parameters.Add(Text(e.Id));
        cmd.Parameters.Add(Text(e.TenantId));
        cmd.Parameters.Add(NullableText(e.ProjectId));
        cmd.Parameters.Add(Text(e.Kind));
        cmd.Parameters.Add(Text(e.Name));
        cmd.Parameters.Add(Text(e.Description));
        cmd.Parameters.Add(Jsonb(Json(e.Properties)));
        cmd.Parameters.Add(Text(e.State));
        cmd.Parameters.Add(Bool(e.Locked));
        cmd.Parameters.Add(Int(e.Version));
        cmd.Parameters.Add(NullableText(e.ProposalId));
        cmd.Parameters.Add(NullableText(e.CounterpartId));
        cmd.Parameters.Add(NullableText(e.ChangeKind));
        cmd.Parameters.Add(Timestamp(e.CreatedAt));
        cmd.Parameters.Add(Timestamp(e.UpdatedAt));
    }

    private static void BindRelationship(NpgsqlCommand cmd, ArchitectureRelationshipRecord rel)
    {
        cmd.Parameters.Add(Text(rel.Id));
        cmd.Parameters.Add(Text(rel.TenantId));
        cmd.Parameters.Add(NullableText(rel.ProjectId));
        cmd.Parameters.Add(Text(rel.SourceId));
        cmd.Parameters.Add(Text(rel.TargetId));
        cmd.Parameters.Add(Text(rel.Kind));
        cmd.Parameters.Add(Jsonb(Json(rel.Properties)));
        cmd.Parameters.Add(Text(rel.State));
        cmd.Parameters.Add(Int(rel.Version));
        cmd.Parameters.Add(NullableText(rel.ProposalId));
        cmd.Parameters.Add(NullableText(rel.CounterpartId));
        cmd.Parameters.Add(NullableText(rel.ChangeKind));
        cmd.Parameters.Add(Timestamp(rel.CreatedAt));
        cmd.Parameters.Add(Timestamp(rel.UpdatedAt));
    }

    private static void BindProposal(NpgsqlCommand cmd, ArchitectureProposalRecord p)
    {
        cmd.Parameters.Add(Text(p.Id));
        cmd.Parameters.Add(Text(p.TenantId));
        cmd.Parameters.Add(NullableText(p.ProjectId));
        cmd.Parameters.Add(Text(p.Title));
        cmd.Parameters.Add(Text(p.Status));
        cmd.Parameters.Add(NullableText(p.Justification));
        cmd.Parameters.Add(Timestamp(p.CreatedAt));
        cmd.Parameters.Add(NullableTimestamp(p.AppliedAt));
    }

    private static ArchitectureElementRecord ReadElement(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), Nullable(r, 2), r.GetString(3),
        r.GetString(4), r.GetString(5), ReadProperties(r.GetString(6)), r.GetString(7),
        r.GetBoolean(8), r.GetInt32(9), Nullable(r, 10), Nullable(r, 11), Nullable(r, 12),
        r.GetFieldValue<DateTimeOffset>(13), r.GetFieldValue<DateTimeOffset>(14));

    private static ArchitectureRelationshipRecord ReadRelationship(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), Nullable(r, 2), r.GetString(3).TrimEnd(),
        r.GetString(4).TrimEnd(), r.GetString(5), ReadProperties(r.GetString(6)), r.GetString(7),
        r.GetInt32(8), Nullable(r, 9), Nullable(r, 10), Nullable(r, 11),
        r.GetFieldValue<DateTimeOffset>(12), r.GetFieldValue<DateTimeOffset>(13));

    private static ArchitectureViewRecord ReadView(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), Nullable(r, 2), r.GetString(3),
        r.GetString(4), r.GetString(5), ReadList(r.GetString(6)), ReadList(r.GetString(7)),
        ReadList(r.GetString(8)), ReadList(r.GetString(9)),
        r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<DateTimeOffset>(11));

    private static ArchitectureProposalRecord ReadProposal(NpgsqlDataReader r) => new(
        r.GetString(0).TrimEnd(), r.GetString(1).TrimEnd(), Nullable(r, 2), r.GetString(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7));

    private static ArchitectureSystemMetadataRecord ReadMetadata(string json) =>
        JsonSerializer.Deserialize<ArchitectureSystemMetadataRecord>(json, JsonOptions)
            ?? throw new InvalidOperationException("Architecture system metadata could not be read back.");

    private static Dictionary<string, string> ReadProperties(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? new Dictionary<string, string>();

    private static List<string> ReadList(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];

    private static string? Nullable(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal).TrimEnd();

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

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };
    private static NpgsqlParameter NullableText(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
    private static NpgsqlParameter<int> Int(int value) => new() { TypedValue = value };
    private static NpgsqlParameter<bool> Bool(bool value) => new() { TypedValue = value };
    private static NpgsqlParameter Jsonb(string value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };
    private static NpgsqlParameter NullableTimestamp(DateTimeOffset? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)value ?? DBNull.Value };
}
