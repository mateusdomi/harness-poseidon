using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Postgres;

public sealed partial class PostgresWorkflowCatalogStore(NpgsqlDataSource dataSource) : IWorkflowCatalogStore
{
    private const string TemplateSelect =
        "SELECT d.tenant_id,d.id,d.name,d.description," +
        "(SELECT v.id FROM harness.workflow_definition_versions v WHERE v.definition_id=d.id AND v.status='published' AND v.archived_at IS NULL ORDER BY v.version DESC LIMIT 1)," +
        "CASE WHEN d.archived_at IS NOT NULL THEN 'archived' WHEN EXISTS(SELECT 1 FROM harness.workflow_definition_versions p WHERE p.definition_id=d.id AND p.status='published') THEN 'published' ELSE 'draft' END," +
        "d.archived_at,d.created_at FROM harness.workflow_definitions d";
    private const string RunSelect =
        "SELECT tenant_id,id,workflow_id,definition_version_id,state,created_at,started_at,completed_at,version " +
        "FROM harness.workflow_runs";
    private const string PhaseSelect =
        "SELECT r.tenant_id,r.id,r.workflow_run_id,d.name,r.phase_order,r.state,r.activated_at,r.completed_at " +
        "FROM harness.workflow_phase_runs r JOIN harness.workflow_phase_definitions d ON d.id=r.phase_definition_id";
    private const string GateSelect =
        "SELECT g.tenant_id,g.id,g.phase_run_id,p.workflow_run_id,d.name,g.state,g.evaluated_at,g.decided_by_profile_id,g.decision_note " +
        "FROM harness.workflow_gate_runs g JOIN harness.workflow_phase_runs p ON p.id=g.phase_run_id " +
        "JOIN harness.workflow_gate_definitions d ON d.id=g.gate_definition_id";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlyList<WorkflowTemplateCatalogRecord>> ListTemplatesAsync(
        string tenantId, string? afterId, int limit, CancellationToken cancellationToken = default)
    {
        var rows = new List<WorkflowTemplateCatalogRecord>();
        await using var query = _dataSource.CreateCommand(
            $"{TemplateSelect} WHERE d.tenant_id=$1 AND ($2 IS NULL OR d.id>$2) ORDER BY d.id LIMIT $3;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(NullableText(afterId));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadTemplate(reader));
        }

        return rows;
    }

    public async Task<WorkflowTemplateCatalogRecord?> GetTemplateAsync(
        string tenantId, string templateId, CancellationToken cancellationToken = default)
    {
        await using var query = _dataSource.CreateCommand(
            $"{TemplateSelect} WHERE d.tenant_id=$1 AND d.id=$2;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(templateId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTemplate(reader) : null;
    }

    public Task<WorkflowTemplateCatalogRecord> CreateTemplateAsync(
        WorkflowTemplateCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateTemplateCoreAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowVersionCatalogRecord>> ListVersionsAsync(
        string tenantId, string? templateId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var ids = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT id FROM harness.workflow_definition_versions WHERE tenant_id=$1 " +
                "AND ($2 IS NULL OR definition_id=$2) " +
                "AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(NullableText(templateId));
            query.Parameters.Add(NullableText(afterId));
            query.Parameters.Add(Integer(limit));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0).TrimEnd());
            }
        }

        var rows = new List<WorkflowVersionCatalogRecord>();
        foreach (var id in ids)
        {
            rows.Add((await ReadVersionAsync(connection, tenantId, id, cancellationToken))!);
        }

        return rows;
    }

    public async Task<WorkflowVersionCatalogRecord?> GetVersionAsync(
        string tenantId, string versionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadVersionAsync(connection, tenantId, versionId, cancellationToken);
    }

    public Task<WorkflowBindingCatalogRecord> CreateBindingAsync(
        WorkflowBindingCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CreateBindingCoreAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowBindingCatalogRecord>> ListBindingsAsync(
        string tenantId, string? projectId, string? afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var ids = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT id FROM harness.workflow_bindings WHERE tenant_id=$1 " +
                "AND ($2 IS NULL OR project_id=$2) AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(NullableText(projectId));
            query.Parameters.Add(NullableText(afterId));
            query.Parameters.Add(Integer(limit));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0).TrimEnd());
            }
        }

        var rows = new List<WorkflowBindingCatalogRecord>();
        foreach (var id in ids)
        {
            rows.Add((await ReadBindingAsync(connection, tenantId, id, cancellationToken))!);
        }

        return rows;
    }

    public async Task<WorkflowBindingCatalogRecord?> GetBindingAsync(
        string tenantId, string workflowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadBindingAsync(connection, tenantId, workflowId, cancellationToken);
    }

    public Task<IReadOnlyList<WorkflowRunCatalogRecord>> ListRunsAsync(
        string tenantId, string? workflowId, string? afterId, int limit,
        CancellationToken cancellationToken = default) => ReadListAsync(
            $"{RunSelect} WHERE tenant_id=$1 AND workflow_id IS NOT NULL " +
            "AND ($2 IS NULL OR workflow_id=$2) AND ($3 IS NULL OR id>$3) ORDER BY id LIMIT $4;",
            tenantId, workflowId, afterId, limit, ReadRun, cancellationToken);

    public Task<WorkflowRunCatalogRecord?> GetRunAsync(
        string tenantId, string runId, CancellationToken cancellationToken = default) => ReadOneAsync(
            $"{RunSelect} WHERE tenant_id=$1 AND id=$2 AND workflow_id IS NOT NULL;",
            tenantId, runId, ReadRun, cancellationToken);

    public Task<IReadOnlyList<WorkflowPhaseCatalogRecord>> ListPhasesAsync(
        string tenantId, string? runId, string? afterId, int limit,
        CancellationToken cancellationToken = default) => ReadListAsync(
            $"{PhaseSelect} WHERE r.tenant_id=$1 AND ($2 IS NULL OR r.workflow_run_id=$2) " +
            "AND ($3 IS NULL OR r.id>$3) ORDER BY r.id LIMIT $4;",
            tenantId, runId, afterId, limit, ReadPhase, cancellationToken);

    public Task<WorkflowPhaseCatalogRecord?> GetPhaseAsync(
        string tenantId, string phaseId, CancellationToken cancellationToken = default) => ReadOneAsync(
            $"{PhaseSelect} WHERE r.tenant_id=$1 AND r.id=$2;",
            tenantId, phaseId, ReadPhase, cancellationToken);

    public Task<IReadOnlyList<WorkflowGateCatalogRecord>> ListGatesAsync(
        string tenantId, string? runId, string? afterId, int limit,
        CancellationToken cancellationToken = default) => ReadListAsync(
            $"{GateSelect} WHERE g.tenant_id=$1 AND ($2 IS NULL OR p.workflow_run_id=$2) " +
            "AND ($3 IS NULL OR g.id>$3) ORDER BY g.id LIMIT $4;",
            tenantId, runId, afterId, limit, ReadGate, cancellationToken);

    public Task<WorkflowGateCatalogRecord?> GetGateAsync(
        string tenantId, string gateId, CancellationToken cancellationToken = default) => ReadOneAsync(
            $"{GateSelect} WHERE g.tenant_id=$1 AND g.id=$2;",
            tenantId, gateId, ReadGate, cancellationToken);

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(
        string sql, string tenant, string? filter, string? after, int limit,
        Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        await using var query = _dataSource.CreateCommand(sql);
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(NullableText(filter));
        query.Parameters.Add(NullableText(after));
        query.Parameters.Add(Integer(limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async Task<T?> ReadOneAsync<T>(
        string sql, string tenant, string id, Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        await using var query = _dataSource.CreateCommand(sql);
        query.Parameters.Add(Text(tenant));
        query.Parameters.Add(Text(id));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? read(reader) : default;
    }

    private async Task<WorkflowBindingCatalogRecord> CreateBindingCoreAsync(
        WorkflowBindingCreateCommand value, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText =
                "SELECT EXISTS(SELECT 1 FROM harness.projects WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL)," +
                "EXISTS(SELECT 1 FROM harness.workflow_definition_versions WHERE tenant_id=$1 AND id=$3 AND definition_id=$4 AND status='published')," +
                "EXISTS(SELECT 1 FROM harness.local_users WHERE tenant_id=$1 AND id=$5);";
            check.Parameters.Add(Text(value.TenantId));
            check.Parameters.Add(Text(value.ProjectId));
            check.Parameters.Add(Text(value.ActiveVersionId));
            check.Parameters.Add(Text(value.TemplateId));
            check.Parameters.Add(Text(value.AcceptedByProfileId));
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.GetBoolean(0))
            {
                throw new WorkflowCatalogReferenceNotFoundException("project");
            }

            if (!reader.GetBoolean(1))
            {
                throw new WorkflowCatalogReferenceNotFoundException("workflow_version");
            }

            if (!reader.GetBoolean(2))
            {
                throw new WorkflowCatalogReferenceNotFoundException("profile");
            }
        }

        try
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.workflow_bindings
                    (id,tenant_id,project_id,definition_id,active_version_id,operation_mode,
                     pause_gates_json,created_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                """,
                cancellationToken,
                Text(value.Id), Text(value.TenantId), Text(value.ProjectId), Text(value.TemplateId),
                Text(value.ActiveVersionId), Text(value.OperationMode),
                Json(JsonSerializer.Serialize(value.SemiautonomousPauseGates, JsonOptions)),
                Timestamp(value.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.workflow_risk_acceptances
                    (id,tenant_id,workflow_id,mode,accepted_by_profile_id,note,accepted_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7);
                """,
                cancellationToken,
                Text(value.RiskAcceptanceId), Text(value.TenantId), Text(value.Id),
                Text(value.OperationMode), Text(value.AcceptedByProfileId),
                Text(value.RiskAcceptanceNote), Timestamp(value.OccurredAt));
        }
        catch (PostgresException exception) when (IsConstraintViolation(exception))
        {
            throw new WorkflowBindingAlreadyExistsException();
        }

        var payload = JsonSerializer.Serialize(new
        {
            workflowId = value.Id,
            projectId = value.ProjectId,
            templateId = value.TemplateId,
            versionId = value.ActiveVersionId,
            mode = value.OperationMode,
        }, JsonOptions);
        await AppendAuditAsync(
            connection, transaction, value.TenantId, "workflow.bound", payload, value.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadBindingAsync(connection, value.TenantId, value.Id, cancellationToken))!;
    }

    private static async Task<WorkflowVersionCatalogRecord?> ReadVersionAsync(
        NpgsqlConnection connection, string tenant, string id, CancellationToken cancellationToken)
    {
        string template;
        int version;
        string phaseConfigs;
        string? defaultMode;
        string transitions;
        string? changelog;
        string status;
        DateTimeOffset? published;
        DateTimeOffset? archived;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT definition_id,version,phase_configs_json::text,default_operation_mode," +
                "transitions_json::text,changelog,status,published_at,archived_at FROM harness.workflow_definition_versions " +
                "WHERE tenant_id=$1 AND id=$2;";
            query.Parameters.Add(Text(tenant));
            query.Parameters.Add(Text(id));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            template = reader.GetString(0).TrimEnd();
            version = reader.GetInt32(1);
            phaseConfigs = reader.GetString(2);
            defaultMode = reader.IsDBNull(3) ? null : reader.GetString(3);
            transitions = reader.GetString(4);
            changelog = reader.IsDBNull(5) ? null : reader.GetString(5);
            status = reader.GetString(6);
            published = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7);
            archived = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8);
        }

        var phases = new List<string>();
        var gates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var phaseIds = new List<(string Id, string Name)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT id,name FROM harness.workflow_phase_definitions WHERE definition_version_id=$1 ORDER BY phase_order;";
            query.Parameters.Add(Text(id));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                phases.Add(reader.GetString(1));
                phaseIds.Add((reader.GetString(0).TrimEnd(), reader.GetString(1)));
            }
        }

        foreach (var phase in phaseIds)
        {
            var names = new List<string>();
            await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT name FROM harness.workflow_gate_definitions WHERE phase_definition_id=$1 ORDER BY gate_key;";
            query.Parameters.Add(Text(phase.Id));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }

            if (names.Count > 0)
            {
                gates[phase.Name] = names;
            }
        }

        return new(tenant, id, template, version, phases, gates, phaseConfigs, defaultMode,
            transitions, changelog, archived is null ? status : "archived", published, archived);
    }

    private static async Task<WorkflowBindingCatalogRecord?> ReadBindingAsync(
        NpgsqlConnection connection, string tenant, string id, CancellationToken cancellationToken)
    {
        string project;
        string template;
        string version;
        string mode;
        string gatesJson;
        DateTimeOffset created;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT project_id,definition_id,active_version_id,operation_mode,pause_gates_json::text,created_at " +
                "FROM harness.workflow_bindings WHERE tenant_id=$1 AND id=$2;";
            query.Parameters.Add(Text(tenant));
            query.Parameters.Add(Text(id));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            project = reader.GetString(0).TrimEnd();
            template = reader.GetString(1).TrimEnd();
            version = reader.GetString(2).TrimEnd();
            mode = reader.GetString(3);
            gatesJson = reader.GetString(4);
            created = reader.GetFieldValue<DateTimeOffset>(5);
        }

        var acceptances = new List<WorkflowRiskAcceptanceCatalogRecord>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT mode,accepted_by_profile_id,note,accepted_at FROM harness.workflow_risk_acceptances " +
                "WHERE tenant_id=$1 AND workflow_id=$2 ORDER BY accepted_at,id;";
            query.Parameters.Add(Text(tenant));
            query.Parameters.Add(Text(id));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                acceptances.Add(new(
                    reader.GetString(0), reader.GetString(1).TrimEnd(), reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        return new(tenant, id, project, template, version, mode,
            JsonSerializer.Deserialize<string[]>(gatesJson, JsonOptions) ?? [], acceptances, created);
    }

    private static async Task AppendAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenant, string type,
        string payload, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenant}"));
        long sequence;
        string previous;
        await using (var tail = connection.CreateCommand())
        {
            tail.Transaction = transaction;
            tail.CommandText =
                "SELECT sequence,event_hash FROM harness.audit_ledger WHERE tenant_id=$1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;";
            tail.Parameters.Add(Text(tenant));
            await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
            var exists = await reader.ReadAsync(cancellationToken);
            sequence = exists ? reader.GetInt64(0) + 1 : 1;
            previous = exists ? reader.GetString(1).TrimEnd() : AuditLedgerHash.Genesis;
        }

        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, type, payload, at);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
            """,
            cancellationToken,
            Text(UlidValue.New(at).ToString()), Text(tenant), Bigint(sequence), Text(previous),
            Text(hash), Text(type), Json(payload), Timestamp(at));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenant, string type,
        string payload, DateTimeOffset at, CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection, transaction,
            "INSERT INTO harness.outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($1,$2,$3,$4,$5);",
            cancellationToken,
            Text(UlidValue.New(at).ToString()), Text(tenant), Text(type), Json(payload),
            Timestamp(at));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static WorkflowTemplateCatalogRecord ReadTemplate(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(), reader.GetString(2),
        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4).TrimEnd(),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7));

    private static WorkflowRunCatalogRecord ReadRun(NpgsqlDataReader reader)
    {
        var created = reader.GetFieldValue<DateTimeOffset>(5);
        return new(
            reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(), reader.GetString(3).TrimEnd(),
            reader.GetString(4) == "pending" ? "paused" : reader.GetString(4),
            reader.IsDBNull(6) ? created : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetInt64(8));
    }

    private static WorkflowPhaseCatalogRecord ReadPhase(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
        reader.GetString(2).TrimEnd(), reader.GetString(3), reader.GetInt32(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    private static WorkflowGateCatalogRecord ReadGate(NpgsqlDataReader reader) => new(
        reader.GetString(0).TrimEnd(), reader.GetString(1).TrimEnd(),
        reader.GetString(2).TrimEnd(), reader.GetString(3).TrimEnd(), reader.GetString(4),
        reader.GetString(5), true,
        reader.IsDBNull(7) ? null : reader.GetString(7).TrimEnd(),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static bool IsConstraintViolation(PostgresException exception) =>
        exception.SqlState.StartsWith("23", StringComparison.Ordinal);

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<decimal> Numeric(decimal value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };
}
