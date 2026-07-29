using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteProjectStore(SqliteWriteDispatcher dispatcher) : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ProjectRecord?> GetAsync(string tenantId, string projectId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => ReadAsync(c, tenantId, projectId, t), cancellationToken);
    public Task<IReadOnlyList<ProjectRecord>> ListAsync(string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<ProjectRecord>>(async (c, t) =>
        {
            var values = new List<ProjectRecord>(); await using var q = c.CreateCommand();
            q.CommandText = $"{SelectSql} WHERE tenant_id=$tenant AND deleted_at IS NULL AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
            Add(q, "$tenant", tenantId); Add(q, "$after", afterId is null ? DBNull.Value : afterId); Add(q, "$limit", limit);
            await using var reader = await q.ExecuteReaderAsync(t); while (await reader.ReadAsync(t)) values.Add(Read(reader)); return values;
        }, cancellationToken);
    public Task<ProjectMutationResult> CreateAsync(ProjectCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => CreateCoreAsync(c, command, t), cancellationToken);
    public Task<ProjectMutationResult> UpdateAsync(ProjectUpdateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, t) => UpdateCoreAsync(c, command, t), cancellationToken);
    public Task<ProjectMutationResult> DeleteAsync(string tenantId, string projectId, long expectedVersion, DateTimeOffset occurredAt, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<ProjectMutationResult>(async (c, t) =>
        {
            await using var q = c.CreateCommand(); q.CommandText = "UPDATE projects SET deleted_at=$at,version=version+1 WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL AND version=$version;";
            Add(q, "$at", Store(occurredAt)); Add(q, "$tenant", tenantId); Add(q, "$id", projectId); Add(q, "$version", expectedVersion);
            if (await q.ExecuteNonQueryAsync(t) == 1) return new(ProjectMutationStatus.Applied);
            return new(await ReadAsync(c, tenantId, projectId, t) is null ? ProjectMutationStatus.NotFound : ProjectMutationStatus.VersionConflict);
        }, cancellationToken);

    private static async Task<ProjectMutationResult> CreateCoreAsync(SqliteConnection c, ProjectCreateCommand command, CancellationToken token)
    {
        var p = command.Project; await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await using (var exists = c.CreateCommand()) { exists.Transaction = tx; exists.CommandText = "SELECT EXISTS(SELECT 1 FROM organizations WHERE tenant_id=$tenant AND id=$org);"; Add(exists, "$tenant", command.TenantId); Add(exists, "$org", p.OrganizationId); if (Convert.ToInt64(await exists.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 0) { await tx.CommitAsync(token); return new(ProjectMutationStatus.OrganizationNotFound); } }
        try
        {
            await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = """
                INSERT INTO projects (id,tenant_id,organization_id,name,project_key,description,state,criticality,
                    repository_url,repository_provider,default_branch,technologies_json,logo_url,primary_color,
                    secondary_color,typography,member_profile_ids_json,config_version,chief_agent_id,operation_mode,
                    prototyping_mode,prototyping_waiver_reason,prototyping_waiver_granted_at,target_deadline,version,created_at,last_activity_at)
                VALUES ($id,$tenant,$org,$name,$key,$description,$state,$criticality,$url,$provider,$branch,$tech,
                    $logo,$primary,$secondary,$typography,$members,$config,$chief,$mode,$prototypeMode,$waiverReason,$waiverAt,$targetDeadline,1,$at,$at);
                """; Bind(q, p); Add(q, "$tenant", command.TenantId); Add(q, "$at", Store(command.OccurredAt)); await q.ExecuteNonQueryAsync(token);
            await using var chief = c.CreateCommand(); chief.Transaction = tx; chief.CommandText = """
                INSERT INTO agents
                    (id,tenant_id,definition_id,project_id,name,state,lease_fencing_token,
                     lease_expires_at,last_heartbeat_at,created_at)
                VALUES ($id,$tenant,'01ARZ3NDEKTSV4RRFFQ69G5FAV',$project,$name,'idle',1,$lease,$at,$at);
                """;
            Add(chief, "$id", p.ChiefAgentId);
            Add(chief, "$tenant", command.TenantId);
            Add(chief, "$project", p.Id);
            Add(chief, "$name", $"Chief — {p.Key}");
            Add(chief, "$lease", Store(command.OccurredAt.AddMinutes(1)));
            Add(chief, "$at", Store(command.OccurredAt));
            await chief.ExecuteNonQueryAsync(token);
            var payload = JsonSerializer.Serialize(new { projectId = p.Id, organizationId = p.OrganizationId, name = p.Name, key = p.Key });
            var tail = await TailAsync(c, tx, command.TenantId, token); const string eventType = "project.created";
            var hash = AuditLedgerHash.Compute(tail.PreviousHash, command.TenantId, tail.Sequence, eventType, payload, command.OccurredAt);
            await using var events = c.CreateCommand(); events.Transaction = tx; events.CommandText = """
                INSERT INTO audit_ledger (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
                VALUES ($ledger,$tenant,$sequence,$previous,$hash,$type,$payload,$at);
                INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at)
                VALUES ($outbox,$tenant,$type,$payload,$at);
                """; Add(events, "$ledger", UlidValue.New(command.OccurredAt).ToString()); Add(events, "$outbox", UlidValue.New(command.OccurredAt.AddTicks(1)).ToString()); Add(events, "$tenant", command.TenantId); Add(events, "$sequence", tail.Sequence); Add(events, "$previous", tail.PreviousHash); Add(events, "$hash", hash); Add(events, "$type", eventType); Add(events, "$payload", payload); Add(events, "$at", Store(command.OccurredAt)); await events.ExecuteNonQueryAsync(token);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { await tx.RollbackAsync(token); return new(ProjectMutationStatus.AlreadyExists); }
        await tx.CommitAsync(token); return new(ProjectMutationStatus.Applied, await ReadAsync(c, command.TenantId, p.Id, token));
    }

    private static async Task<ProjectMutationResult> UpdateCoreAsync(SqliteConnection c, ProjectUpdateCommand command, CancellationToken token)
    {
        var p = command.Project;
        try { await using var q = c.CreateCommand(); q.CommandText = """
            UPDATE projects SET name=$name,description=$description,state=$state,criticality=$criticality,
              repository_url=$url,repository_provider=$provider,default_branch=$branch,technologies_json=$tech,
              logo_url=$logo,primary_color=$primary,secondary_color=$secondary,typography=$typography,
              member_profile_ids_json=$members,config_version=$config,prototyping_mode=$prototypeMode,
              prototyping_waiver_reason=$waiverReason,prototyping_waiver_granted_at=$waiverAt,target_deadline=$targetDeadline,
              last_activity_at=$last,version=version+1
            WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL AND version=$expected;
            """; Bind(q, p); Add(q, "$tenant", p.TenantId); Add(q, "$last", Store(p.LastActivityAt)); Add(q, "$expected", command.ExpectedVersion); if (await q.ExecuteNonQueryAsync(token) == 1) return new(ProjectMutationStatus.Applied, await ReadAsync(c, p.TenantId, p.Id, token)); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return new(ProjectMutationStatus.AlreadyExists); }
        return new(await ReadAsync(c, p.TenantId, p.Id, token) is null ? ProjectMutationStatus.NotFound : ProjectMutationStatus.VersionConflict);
    }

    private static async Task<ProjectRecord?> ReadAsync(SqliteConnection c, string tenant, string id, CancellationToken token) { await using var q = c.CreateCommand(); q.CommandText = $"{SelectSql} WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL;"; Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? Read(r) : null; }
    private static ProjectRecord Read(SqliteDataReader r) => new ProjectRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetString(10), JsonSerializer.Deserialize<string[]>(r.GetString(11), JsonOptions)!, new(r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15)), JsonSerializer.Deserialize<string[]>(r.GetString(16), JsonOptions)!, r.GetInt64(17), r.GetString(18), r.GetString(19), DateTimeOffset.Parse(r.GetString(20), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(21), CultureInfo.InvariantCulture), r.GetInt64(22)) { Prototyping = new(r.GetString(23), r.IsDBNull(24) ? null : new(r.GetString(24), DateTimeOffset.Parse(r.GetString(25), CultureInfo.InvariantCulture))), TargetDeadline = r.IsDBNull(26) ? null : DateTimeOffset.Parse(r.GetString(26), CultureInfo.InvariantCulture) };
    private static void Bind(SqliteCommand q, ProjectRecord p) { Add(q, "$id", p.Id); Add(q, "$org", p.OrganizationId); Add(q, "$name", p.Name); Add(q, "$key", p.Key); Add(q, "$description", p.Description); Add(q, "$state", p.State); Add(q, "$criticality", p.Criticality); Add(q, "$url", p.RepositoryUrl is null ? DBNull.Value : p.RepositoryUrl); Add(q, "$provider", p.RepositoryProvider); Add(q, "$branch", p.DefaultBranch); Add(q, "$tech", JsonSerializer.Serialize(p.Technologies, JsonOptions)); Add(q, "$logo", p.Brand.LogoUrl is null ? DBNull.Value : p.Brand.LogoUrl); Add(q, "$primary", p.Brand.PrimaryColor is null ? DBNull.Value : p.Brand.PrimaryColor); Add(q, "$secondary", p.Brand.SecondaryColor is null ? DBNull.Value : p.Brand.SecondaryColor); Add(q, "$typography", p.Brand.Typography is null ? DBNull.Value : p.Brand.Typography); Add(q, "$members", JsonSerializer.Serialize(p.MemberProfileIds, JsonOptions)); Add(q, "$config", p.ConfigVersion); Add(q, "$chief", p.ChiefAgentId); Add(q, "$mode", p.OperationMode); Add(q, "$prototypeMode", p.Prototyping.Mode); Add(q, "$waiverReason", p.Prototyping.Waiver?.Reason ?? (object)DBNull.Value); Add(q, "$waiverAt", p.Prototyping.Waiver is null ? DBNull.Value : Store(p.Prototyping.Waiver.GrantedAt)); Add(q, "$targetDeadline", p.TargetDeadline is null ? DBNull.Value : Store(p.TargetDeadline.Value)); }
    private static async Task<(long Sequence, string PreviousHash)> TailAsync(SqliteConnection c, SqliteTransaction tx, string tenant, CancellationToken token) { await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;"; Add(q, "$tenant", tenant); await using var r = await q.ExecuteReaderAsync(token); return await r.ReadAsync(token) ? (r.GetInt64(0) + 1, r.GetString(1)) : (1, AuditLedgerHash.Genesis); }
    private const string SelectSql = "SELECT tenant_id,id,organization_id,name,project_key,description,state,criticality,repository_url,repository_provider,default_branch,technologies_json,logo_url,primary_color,secondary_color,typography,member_profile_ids_json,config_version,chief_agent_id,operation_mode,created_at,COALESCE(last_activity_at,created_at),version,prototyping_mode,prototyping_waiver_reason,prototyping_waiver_granted_at,target_deadline FROM projects";
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand q, string name, object value) => q.Parameters.AddWithValue(name, value);
}
