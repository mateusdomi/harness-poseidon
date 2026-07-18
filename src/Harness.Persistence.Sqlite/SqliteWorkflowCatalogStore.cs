using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteWorkflowCatalogStore(SqliteWriteDispatcher dispatcher) : IWorkflowCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<IReadOnlyList<WorkflowTemplateCatalogRecord>> ListTemplatesAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<WorkflowTemplateCatalogRecord>>(async (c, token) =>
        {
            var rows = new List<WorkflowTemplateCatalogRecord>(); await using var q = c.CreateCommand();
            q.CommandText = TemplateSelect +
                " WHERE d.tenant_id=$tenant AND ($after IS NULL OR d.id>$after) ORDER BY d.id LIMIT $limit;";
            Add(q, "$tenant", tenantId); AddNullable(q, "$after", afterId); Add(q, "$limit", limit);
            await using var r = await q.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) rows.Add(ReadTemplate(r)); return rows;
        }, cancellationToken);

    public Task<WorkflowTemplateCatalogRecord?> GetTemplateAsync(
        string tenantId, string templateId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync(async (c, token) =>
        {
            await using var q = c.CreateCommand(); q.CommandText = TemplateSelect +
                " WHERE d.tenant_id=$tenant AND d.id=$id;";
            Add(q, "$tenant", tenantId); Add(q, "$id", templateId);
            await using var r = await q.ExecuteReaderAsync(token);
            return await r.ReadAsync(token) ? ReadTemplate(r) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<WorkflowVersionCatalogRecord>> ListVersionsAsync(
        string tenantId, string? templateId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<WorkflowVersionCatalogRecord>>(async (c, token) =>
        {
            var ids = new List<string>(); await using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT id FROM workflow_definition_versions WHERE tenant_id=$tenant " +
                    "AND status='published' AND ($template IS NULL OR definition_id=$template) " +
                    "AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
                Add(q, "$tenant", tenantId); AddNullable(q, "$template", templateId);
                AddNullable(q, "$after", afterId); Add(q, "$limit", limit);
                await using var r = await q.ExecuteReaderAsync(token);
                while (await r.ReadAsync(token)) ids.Add(r.GetString(0));
            }
            var rows = new List<WorkflowVersionCatalogRecord>();
            foreach (var id in ids) rows.Add((await ReadVersionAsync(c, tenantId, id, token))!);
            return rows;
        }, cancellationToken);

    public Task<WorkflowVersionCatalogRecord?> GetVersionAsync(
        string tenantId, string versionId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => ReadVersionAsync(c, tenantId, versionId, token), cancellationToken);

    public Task<WorkflowBindingCatalogRecord> CreateBindingAsync(
        WorkflowBindingCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => CreateBindingCoreAsync(c, command, token), cancellationToken);

    public Task<IReadOnlyList<WorkflowBindingCatalogRecord>> ListBindingsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync<IReadOnlyList<WorkflowBindingCatalogRecord>>(async (c, token) =>
        {
            var ids = new List<string>(); await using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT id FROM workflow_bindings WHERE tenant_id=$tenant " +
                    "AND ($project IS NULL OR project_id=$project) AND ($after IS NULL OR id>$after) " +
                    "ORDER BY id LIMIT $limit;";
                Add(q, "$tenant", tenantId); AddNullable(q, "$project", projectId);
                AddNullable(q, "$after", afterId); Add(q, "$limit", limit);
                await using var r = await q.ExecuteReaderAsync(token);
                while (await r.ReadAsync(token)) ids.Add(r.GetString(0));
            }
            var rows = new List<WorkflowBindingCatalogRecord>();
            foreach (var id in ids) rows.Add((await ReadBindingAsync(c, tenantId, id, token))!);
            return rows;
        }, cancellationToken);

    public Task<WorkflowBindingCatalogRecord?> GetBindingAsync(
        string tenantId, string workflowId, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((c, token) => ReadBindingAsync(c, tenantId, workflowId, token), cancellationToken);

    public Task<IReadOnlyList<WorkflowRunCatalogRecord>> ListRunsAsync(
        string tenantId, string? workflowId, string? afterId, int limit,
        CancellationToken cancellationToken = default) => ReadListAsync(
            "SELECT tenant_id,id,workflow_id,definition_version_id,state,created_at,started_at,completed_at,version " +
            "FROM workflow_runs WHERE tenant_id=$tenant AND workflow_id IS NOT NULL " +
            "AND ($filter IS NULL OR workflow_id=$filter) AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;",
            tenantId, workflowId, afterId, limit, ReadRun, cancellationToken);

    public Task<WorkflowRunCatalogRecord?> GetRunAsync(
        string tenantId, string runId, CancellationToken cancellationToken = default) => ReadOneAsync(
            "SELECT tenant_id,id,workflow_id,definition_version_id,state,created_at,started_at,completed_at,version " +
            "FROM workflow_runs WHERE tenant_id=$tenant AND id=$id AND workflow_id IS NOT NULL;",
            tenantId, runId, ReadRun, cancellationToken);

    public Task<IReadOnlyList<WorkflowPhaseCatalogRecord>> ListPhasesAsync(
        string tenantId, string? runId, string? afterId, int limit,
        CancellationToken cancellationToken = default) => ReadListAsync(
            "SELECT r.tenant_id,r.id,r.workflow_run_id,d.name,r.phase_order,r.state,r.activated_at,r.completed_at " +
            "FROM workflow_phase_runs r JOIN workflow_phase_definitions d ON d.id=r.phase_definition_id " +
            "WHERE r.tenant_id=$tenant AND ($filter IS NULL OR r.workflow_run_id=$filter) " +
            "AND ($after IS NULL OR r.id>$after) ORDER BY r.id LIMIT $limit;",
            tenantId, runId, afterId, limit, ReadPhase, cancellationToken);

    public Task<WorkflowPhaseCatalogRecord?> GetPhaseAsync(
        string tenantId, string phaseId, CancellationToken cancellationToken = default) => ReadOneAsync(
            "SELECT r.tenant_id,r.id,r.workflow_run_id,d.name,r.phase_order,r.state,r.activated_at,r.completed_at " +
            "FROM workflow_phase_runs r JOIN workflow_phase_definitions d ON d.id=r.phase_definition_id " +
            "WHERE r.tenant_id=$tenant AND r.id=$id;", tenantId, phaseId, ReadPhase, cancellationToken);

    public Task<IReadOnlyList<WorkflowGateCatalogRecord>> ListGatesAsync(
        string tenantId, string? runId, string? afterId, int limit,
        CancellationToken cancellationToken = default) => ReadListAsync(
            GateSelect + " WHERE g.tenant_id=$tenant AND ($filter IS NULL OR p.workflow_run_id=$filter) " +
            "AND ($after IS NULL OR g.id>$after) ORDER BY g.id LIMIT $limit;",
            tenantId, runId, afterId, limit, ReadGate, cancellationToken);

    public Task<WorkflowGateCatalogRecord?> GetGateAsync(
        string tenantId, string gateId, CancellationToken cancellationToken = default) => ReadOneAsync(
            GateSelect + " WHERE g.tenant_id=$tenant AND g.id=$id;",
            tenantId, gateId, ReadGate, cancellationToken);

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(string sql, string tenant, string? filter,
        string? after, int limit, Func<SqliteDataReader, T> read, CancellationToken cancellationToken) =>
        await _dispatcher.ExecuteAsync<IReadOnlyList<T>>(async (c, token) =>
        {
            var rows = new List<T>(); await using var q = c.CreateCommand(); q.CommandText = sql;
            Add(q, "$tenant", tenant); AddNullable(q, "$filter", filter); AddNullable(q, "$after", after);
            Add(q, "$limit", limit); await using var r = await q.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) rows.Add(read(r)); return rows;
        }, cancellationToken);

    private async Task<T?> ReadOneAsync<T>(string sql, string tenant, string id,
        Func<SqliteDataReader, T> read, CancellationToken cancellationToken) =>
        await _dispatcher.ExecuteAsync<T?>(async (c, token) =>
        {
            await using var q = c.CreateCommand(); q.CommandText = sql;
            Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token);
            return await r.ReadAsync(token) ? read(r) : default;
        }, cancellationToken);

    private static async Task<WorkflowBindingCatalogRecord> CreateBindingCoreAsync(
        SqliteConnection c, WorkflowBindingCreateCommand value, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(token);
        await using (var check = c.CreateCommand())
        {
            check.Transaction = tx; check.CommandText =
                "SELECT EXISTS(SELECT 1 FROM projects WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL)," +
                "EXISTS(SELECT 1 FROM workflow_definition_versions WHERE tenant_id=$tenant AND id=$version AND definition_id=$template AND status='published')," +
                "EXISTS(SELECT 1 FROM local_users WHERE tenant_id=$tenant AND id=$profile);";
            Add(check, "$tenant", value.TenantId); Add(check, "$project", value.ProjectId);
            Add(check, "$version", value.ActiveVersionId); Add(check, "$template", value.TemplateId);
            Add(check, "$profile", value.AcceptedByProfileId); await using var r = await check.ExecuteReaderAsync(token);
            await r.ReadAsync(token);
            if (r.GetInt64(0) == 0) throw new WorkflowCatalogReferenceNotFoundException("project");
            if (r.GetInt64(1) == 0) throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            if (r.GetInt64(2) == 0) throw new WorkflowCatalogReferenceNotFoundException("profile");
        }
        try
        {
            await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText =
                "INSERT INTO workflow_bindings " +
                "(id,tenant_id,project_id,definition_id,active_version_id,operation_mode,pause_gates_json,created_at) " +
                "VALUES ($id,$tenant,$project,$template,$version,$mode,$gates,$at); " +
                "INSERT INTO workflow_risk_acceptances " +
                "(id,tenant_id,workflow_id,mode,accepted_by_profile_id,note,accepted_at) " +
                "VALUES ($acceptance,$tenant,$id,$mode,$profile,$note,$at);";
            Add(q, "$id", value.Id); Add(q, "$tenant", value.TenantId); Add(q, "$project", value.ProjectId);
            Add(q, "$template", value.TemplateId); Add(q, "$version", value.ActiveVersionId);
            Add(q, "$mode", value.OperationMode); Add(q, "$gates", JsonSerializer.Serialize(value.SemiautonomousPauseGates, JsonOptions));
            Add(q, "$at", Store(value.OccurredAt)); Add(q, "$acceptance", value.RiskAcceptanceId);
            Add(q, "$profile", value.AcceptedByProfileId); Add(q, "$note", value.RiskAcceptanceNote);
            await q.ExecuteNonQueryAsync(token);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new WorkflowBindingAlreadyExistsException();
        }
        var payload = JsonSerializer.Serialize(new
        {
            workflowId = value.Id,
            projectId = value.ProjectId,
            templateId = value.TemplateId,
            versionId = value.ActiveVersionId,
            mode = value.OperationMode
        }, JsonOptions);
        await AppendAuditAsync(c, tx, value.TenantId, "workflow.bound", payload, value.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadBindingAsync(c, value.TenantId, value.Id, token))!;
    }

    private static async Task<WorkflowVersionCatalogRecord?> ReadVersionAsync(
        SqliteConnection c, string tenant, string id, CancellationToken token)
    {
        string template; int version; DateTimeOffset published;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT definition_id,version,published_at FROM workflow_definition_versions " +
                "WHERE tenant_id=$tenant AND id=$id AND status='published';";
            Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) return null;
            template = r.GetString(0); version = r.GetInt32(1); published = Parse(r.GetString(2));
        }
        var phases = new List<string>(); var gates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var phaseIds = new List<(string Id, string Name)>(); await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id,name FROM workflow_phase_definitions WHERE definition_version_id=$id ORDER BY phase_order;";
            Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) { phases.Add(r.GetString(1)); phaseIds.Add((r.GetString(0), r.GetString(1))); }
        }
        foreach (var phase in phaseIds)
        {
            var names = new List<string>(); await using var q = c.CreateCommand();
            q.CommandText = "SELECT name FROM workflow_gate_definitions WHERE phase_definition_id=$id ORDER BY gate_key;";
            Add(q, "$id", phase.Id); await using var r = await q.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) names.Add(r.GetString(0)); if (names.Count > 0) gates[phase.Name] = names;
        }
        return new(tenant, id, template, version, phases, gates, null, published);
    }

    private static async Task<WorkflowBindingCatalogRecord?> ReadBindingAsync(
        SqliteConnection c, string tenant, string id, CancellationToken token)
    {
        string project; string template; string version; string mode; string gatesJson; DateTimeOffset created;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT project_id,definition_id,active_version_id,operation_mode,pause_gates_json,created_at " +
                "FROM workflow_bindings WHERE tenant_id=$tenant AND id=$id;";
            Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) return null;
            project = r.GetString(0); template = r.GetString(1); version = r.GetString(2); mode = r.GetString(3);
            gatesJson = r.GetString(4); created = Parse(r.GetString(5));
        }
        var acceptances = new List<WorkflowRiskAcceptanceCatalogRecord>(); await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT mode,accepted_by_profile_id,note,accepted_at FROM workflow_risk_acceptances " +
                "WHERE tenant_id=$tenant AND workflow_id=$id ORDER BY accepted_at,id;";
            Add(q, "$tenant", tenant); Add(q, "$id", id); await using var r = await q.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) acceptances.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), Parse(r.GetString(3))));
        }
        return new(tenant, id, project, template, version, mode,
            JsonSerializer.Deserialize<string[]>(gatesJson, JsonOptions) ?? [], acceptances, created);
    }

    private static async Task AppendAuditAsync(SqliteConnection c, SqliteTransaction tx, string tenant,
        string type, string payload, DateTimeOffset at, CancellationToken token)
    {
        long sequence; string previous; await using (var tail = c.CreateCommand())
        {
            tail.Transaction = tx; tail.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
            Add(tail, "$tenant", tenant); await using var r = await tail.ExecuteReaderAsync(token); var exists = await r.ReadAsync(token);
            sequence = exists ? r.GetInt64(0) + 1 : 1; previous = exists ? r.GetString(1) : AuditLedgerHash.Genesis;
        }
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at); await using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT INTO audit_ledger (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES ($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);";
        Add(q, "$id", UlidValue.New(at).ToString()); Add(q, "$tenant", tenant); Add(q, "$sequence", sequence); Add(q, "$previous", previous);
        Add(q, "$hash", hash); Add(q, "$type", type); Add(q, "$payload", payload); Add(q, "$at", Store(at)); await q.ExecuteNonQueryAsync(token);
    }

    private static WorkflowTemplateCatalogRecord ReadTemplate(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), Parse(r.GetString(5)));
    private static WorkflowRunCatalogRecord ReadRun(SqliteDataReader r)
    {
        var created = Parse(r.GetString(5)); return new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4) == "pending" ? "paused" : r.GetString(4),
        r.IsDBNull(6) ? created : Parse(r.GetString(6)), r.IsDBNull(7) ? null : Parse(r.GetString(7)), r.GetInt64(8));
    }
    private static WorkflowPhaseCatalogRecord ReadPhase(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5),
        r.IsDBNull(6) ? null : Parse(r.GetString(6)), r.IsDBNull(7) ? null : Parse(r.GetString(7)));
    private static WorkflowGateCatalogRecord ReadGate(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), true,
        r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(6) ? null : Parse(r.GetString(6)), r.IsDBNull(8) ? null : r.GetString(8));
    private const string TemplateSelect = "SELECT d.tenant_id,d.id,d.name,d.description," +
        "(SELECT v.id FROM workflow_definition_versions v WHERE v.definition_id=d.id AND v.status='published' ORDER BY v.version DESC LIMIT 1),d.created_at FROM workflow_definitions d";
    private const string GateSelect = "SELECT g.tenant_id,g.id,g.phase_run_id,p.workflow_run_id,d.name,g.state,g.evaluated_at,g.decided_by_profile_id,g.decision_note " +
        "FROM workflow_gate_runs g JOIN workflow_phase_runs p ON p.id=g.phase_run_id JOIN workflow_gate_definitions d ON d.id=g.gate_definition_id";
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand q, string name, object value) => q.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand q, string name, object? value) => q.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
