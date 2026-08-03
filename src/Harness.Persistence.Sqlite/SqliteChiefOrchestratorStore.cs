using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteChiefOrchestratorStore(SqliteWriteDispatcher dispatcher) : IChiefOrchestratorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<ProjectRecord> PauseAsync(ChiefProjectCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => SetPauseStateAsync(connection, command, true, token), cancellationToken);

    public Task<ProjectRecord> ResumeAsync(ChiefProjectCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => SetPauseStateAsync(connection, command, false, token), cancellationToken);

    public Task<AgentRecord> HandoffAsync(ChiefHandoffCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => HandoffCoreAsync(connection, command, token), cancellationToken);

    public Task<int> DrainAsync(ChiefDrainCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => DrainCoreAsync(connection, command, token), cancellationToken);

    private static async Task<ProjectRecord> SetPauseStateAsync(
        SqliteConnection connection, ChiefProjectCommand command, bool pause, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var project = await ReadProjectAsync(connection, tx, command.TenantId, command.ProjectId, token)
            ?? throw new ChiefResourceNotFoundException("project");
        if (project.State == "archived") throw new ChiefStateConflictException("An archived project cannot change orchestration state.");
        var chief = await ReadAgentAsync(connection, tx, command.TenantId, project.ChiefAgentId, token)
            ?? throw new ChiefResourceNotFoundException("chief_agent");
        var projectState = pause ? "paused" : "active";
        var agentState = pause ? "waiting" : "idle";
        if (project.State == projectState && chief.State == agentState)
        {
            await tx.CommitAsync(token);
            return project;
        }

        await ExecuteAsync(connection, tx,
            "UPDATE projects SET state=$state,last_activity_at=$at,version=version+1 WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL; " +
            "UPDATE agents SET state=$agentState,current_task_id=NULL,last_heartbeat_at=$at WHERE tenant_id=$tenant AND id=$agent;",
            token,
            ("$state", projectState), ("$agentState", agentState), ("$at", Store(command.OccurredAt)),
            ("$tenant", command.TenantId), ("$project", command.ProjectId), ("$agent", chief.Id));
        if (chief.State != agentState)
            await AppendAgentEventAsync(connection, tx, command.TenantId, chief.Id, chief.State, agentState, null, command.OccurredAt, token);
        var action = pause ? "chief.paused" : "chief.resumed";
        // Pausar interrompe o DESPACHO; não alcança a tentativa que já está em execução (para
        // isso existe o drain). Silenciar essa diferença fez a pausa de 2026-08-03 parecer
        // total enquanto duas tentativas seguiam vivas segurando a única conta disponível.
        var inFlight = pause ? await CountRunningAttemptsAsync(connection, tx, command, token) : 0;
        var detail = pause
            ? inFlight == 0
                ? "Project orchestration paused."
                : $"Project orchestration paused. {inFlight} attempt(s) already running continue until they finish; use drain to cancel them."
            : "Project orchestration resumed.";
        await AppendAuditEventAsync(connection, tx, command.TenantId, command.ProjectId,
            command.ActorProfileId, action, detail, command.OccurredAt, token);
        await tx.CommitAsync(token);
        return (await ReadProjectAsync(connection, null, command.TenantId, command.ProjectId, token))!;
    }

    private static async Task<int> CountRunningAttemptsAsync(
        SqliteConnection connection, SqliteTransaction tx, ChiefProjectCommand command, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = tx;
        query.CommandText =
            "SELECT COUNT(*) FROM work_attempts WHERE tenant_id=$tenant AND project_id=$project AND operational_state='running';";
        Add(query, "$tenant", command.TenantId);
        Add(query, "$project", command.ProjectId);
        return Convert.ToInt32(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task<AgentRecord> HandoffCoreAsync(
        SqliteConnection connection, ChiefHandoffCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var project = await ReadProjectAsync(connection, tx, command.TenantId, command.ProjectId, token)
            ?? throw new ChiefResourceNotFoundException("project");
        if (project.State == "archived") throw new ChiefStateConflictException("An archived project cannot hand off orchestration.");
        var oldChief = await ReadAgentAsync(connection, tx, command.TenantId, project.ChiefAgentId, token)
            ?? throw new ChiefResourceNotFoundException("chief_agent");
        var definitionId = command.TargetDefinitionId ?? oldChief.DefinitionId;
        await using (var definition = connection.CreateCommand())
        {
            definition.Transaction = tx;
            definition.CommandText = "SELECT role FROM agent_definitions WHERE id=$id;";
            Add(definition, "$id", definitionId);
            var role = await definition.ExecuteScalarAsync(token) as string;
            if (role is null) throw new ChiefResourceNotFoundException("agent_definition");
            if (role != "chief") throw new ChiefStateConflictException("The target definition must have the chief role.");
        }
        if (command.TargetModelId is not null)
        {
            await using var model = connection.CreateCommand();
            model.Transaction = tx;
            model.CommandText = "SELECT enabled FROM provider_models WHERE tenant_id=$tenant AND id=$id;";
            Add(model, "$tenant", command.TenantId);
            Add(model, "$id", command.TargetModelId);
            var enabled = await model.ExecuteScalarAsync(token);
            if (enabled is null) throw new ChiefResourceNotFoundException("model");
            if (Convert.ToInt32(enabled, CultureInfo.InvariantCulture) != 1)
                throw new ChiefStateConflictException("The target model is disabled.");
        }

        long fencing;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT COALESCE(MAX(lease_fencing_token),0)+1 FROM agents WHERE tenant_id=$tenant AND project_id=$project;";
            Add(query, "$tenant", command.TenantId); Add(query, "$project", command.ProjectId);
            fencing = Convert.ToInt64(await query.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }

        await ExecuteAsync(connection, tx,
            "UPDATE agents SET state='idle',current_task_id=NULL,lease_fencing_token=NULL,lease_expires_at=NULL WHERE tenant_id=$tenant AND id=$old; " +
            "INSERT INTO agents (id,tenant_id,definition_id,project_id,name,state,current_task_id,model_id,lease_fencing_token,lease_expires_at,last_heartbeat_at,created_at) " +
            "VALUES ($new,$tenant,$definition,$project,$name,'idle',NULL,$model,$fencing,$expires,$at,$at); " +
            "UPDATE projects SET chief_agent_id=$new,last_activity_at=$at,version=version+1 WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL;",
            token,
            ("$tenant", command.TenantId), ("$old", oldChief.Id), ("$new", command.NewAgentId),
            ("$definition", definitionId), ("$project", command.ProjectId), ("$name", oldChief.Name),
            ("$model", command.TargetModelId ?? (object)DBNull.Value), ("$fencing", fencing),
            ("$expires", Store(command.OccurredAt.AddMinutes(1))), ("$at", Store(command.OccurredAt)));
        await AppendAgentEventAsync(connection, tx, command.TenantId, oldChief.Id, oldChief.State, "idle", null, command.OccurredAt, token);
        await AppendAgentEventAsync(connection, tx, command.TenantId, command.NewAgentId, "idle", "idle", null, command.OccurredAt.AddTicks(1), token);
        await AppendAuditEventAsync(connection, tx, command.TenantId, command.ProjectId,
            command.ActorProfileId, "chief.handedOff",
            $"Chief handoff completed with fencing {fencing}. Reason: {command.Note}", command.OccurredAt.AddTicks(2), token);
        await tx.CommitAsync(token);
        return (await ReadAgentAsync(connection, null, command.TenantId, command.NewAgentId, token))!;
    }

    private static async Task<int> DrainCoreAsync(
        SqliteConnection connection, ChiefDrainCommand command, CancellationToken token)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        _ = await ReadProjectAsync(connection, tx, command.TenantId, command.ProjectId, token)
            ?? throw new ChiefResourceNotFoundException("project");
        var tasks = new List<(string Id, string State)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT id,board_state FROM work_tasks WHERE tenant_id=$tenant AND project_id=$project AND board_state IN ('development','review','corrections','testsGates') ORDER BY id;";
            Add(query, "$tenant", command.TenantId); Add(query, "$project", command.ProjectId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) tasks.Add((reader.GetString(0), reader.GetString(1)));
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        foreach (var task in tasks)
        {
            await ExecuteAsync(connection, tx,
                "UPDATE work_tasks SET state='ready',board_state='ready',blocked_reason=NULL,version=version+1,updated_at=$at WHERE tenant_id=$tenant AND id=$id;",
                token, ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$id", task.Id));
            var payload = JsonSerializer.Serialize(new
            {
                projectId = command.ProjectId,
                taskId = task.Id,
                from = task.State,
                to = "ready",
                changedByKind = "chief",
                note,
            }, JsonOptions);
            await AppendLedgerAsync(connection, tx, command.TenantId, "task.stateChanged", payload, command.OccurredAt, token);
            await AppendOutboxAsync(connection, tx, command.TenantId, "task.stateChanged", payload, command.OccurredAt, token);
        }

        await ExecuteAsync(connection, tx,
            "UPDATE work_attempts SET state='rejected',operational_state='cancelled',completed_at=$at," +
            "duration_ms=MAX(0,CAST((julianday($at)-julianday(started_at))*86400000 AS INTEGER))," +
            "failure_reason='Cancelled by Chief drain.' WHERE tenant_id=$tenant AND project_id=$project AND operational_state='running';",
            token, ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$project", command.ProjectId));

        var agents = new List<(string Id, string State)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT id,state FROM agents WHERE tenant_id=$tenant AND project_id=$project AND retired_at IS NULL AND state IN ('working','waiting') ORDER BY id;";
            Add(query, "$tenant", command.TenantId); Add(query, "$project", command.ProjectId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) agents.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var agent in agents)
        {
            await ExecuteAsync(connection, tx,
                "UPDATE agents SET state='idle',current_task_id=NULL,last_heartbeat_at=$at WHERE tenant_id=$tenant AND id=$id;",
                token, ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$id", agent.Id));
            await AppendAgentEventAsync(connection, tx, command.TenantId, agent.Id, agent.State, "idle", null, command.OccurredAt, token);
        }

        await CancelDurableExecutionsAsync(connection, tx, command, token);
        await ExecuteAsync(connection, tx,
            "UPDATE projects SET last_activity_at=$at,version=version+1 WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL;",
            token, ("$at", Store(command.OccurredAt)), ("$tenant", command.TenantId), ("$project", command.ProjectId));
        await AppendAuditEventAsync(connection, tx, command.TenantId, command.ProjectId,
            command.ActorProfileId, "chief.tasksDrained",
            $"{tasks.Count} task(s) drained to ready.{(note is null ? string.Empty : $" Note: {note}")}",
            command.OccurredAt.AddTicks(3), token);
        await tx.CommitAsync(token);
        return tasks.Count;
    }

    private static async Task CancelDurableExecutionsAsync(
        SqliteConnection connection, SqliteTransaction tx, ChiefDrainCommand command, CancellationToken token)
    {
        var executions = new List<(string Id, string State, string? AttemptId)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT id,state,active_attempt_id FROM durable_executions WHERE tenant_id=$tenant AND project_id=$project AND state NOT IN ('completed','cancelled','dead_letter') ORDER BY id;";
            Add(query, "$tenant", command.TenantId); Add(query, "$project", command.ProjectId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) executions.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        foreach (var execution in executions)
        {
            if (execution.AttemptId is not null)
                await ExecuteAsync(connection, tx,
                    "UPDATE durable_attempts SET state='cancelled',ended_at=$at,error_code='chief_drain',error_detail='Cancelled by Chief drain.',version=version+1 WHERE id=$attempt AND state='running';",
                    token, ("$at", Store(command.OccurredAt)), ("$attempt", execution.AttemptId));
            await ExecuteAsync(connection, tx,
                "UPDATE durable_executions SET state='cancelled',active_attempt_id=NULL,fencing_token=fencing_token+1,last_error='chief_drain',version=version+1,updated_at=$at WHERE id=$id;",
                token, ("$at", Store(command.OccurredAt)), ("$id", execution.Id));
            long sequence;
            await using (var sequenceQuery = connection.CreateCommand())
            {
                sequenceQuery.Transaction = tx;
                sequenceQuery.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM durable_transitions WHERE execution_id=$id;";
                Add(sequenceQuery, "$id", execution.Id);
                sequence = Convert.ToInt64(await sequenceQuery.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            }
            var payload = JsonSerializer.Serialize(new { executionId = execution.Id, attemptId = execution.AttemptId, state = "cancelled", reason = "chief-drain" }, JsonOptions);
            await ExecuteAsync(connection, tx,
                "INSERT INTO durable_transitions (execution_id,sequence,from_state,to_state,reason,attempt_id,occurred_at) VALUES ($id,$sequence,$from,'cancelled','chief-drain',$attempt,$at); " +
                "INSERT INTO durable_execution_outbox (execution_id,transition_sequence,event_type,payload_json,occurred_at) VALUES ($id,$sequence,'durable.stateChanged',$payload,$at);",
                token, ("$id", execution.Id), ("$sequence", sequence), ("$from", execution.State),
                ("$attempt", execution.AttemptId ?? (object)DBNull.Value), ("$at", Store(command.OccurredAt)), ("$payload", payload));
            await AppendLedgerAsync(connection, tx, command.TenantId, "durable.stateChanged", payload, command.OccurredAt, token);
        }
    }

    private static async Task AppendAgentEventAsync(SqliteConnection connection, SqliteTransaction tx,
        string tenant, string agentId, string from, string to, string? currentTaskId,
        DateTimeOffset at, CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(new { agentId, from, to, currentTaskId }, JsonOptions);
        await AppendOutboxAsync(connection, tx, tenant, "agent.statusChanged", payload, at, token);
    }

    private static async Task AppendAuditEventAsync(SqliteConnection connection, SqliteTransaction tx,
        string tenant, string projectId, string actorProfileId, string action, string detail,
        DateTimeOffset at, CancellationToken token)
    {
        var id = UlidValue.New(at).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            projectId,
            auditEvent = new { id, actorKind = "user", actorId = actorProfileId, action, targetType = "project", targetId = projectId, detail, occurredAt = at },
        }, JsonOptions);
        await AppendLedgerAsync(connection, tx, tenant, action, payload, at, token);
        await AppendOutboxAsync(connection, tx, tenant, "audit.eventAppended", payload, at, token);
    }

    private static async Task AppendLedgerAsync(SqliteConnection connection, SqliteTransaction tx,
        string tenant, string eventType, string payload, DateTimeOffset at, CancellationToken token)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        long sequence; string previous;
        await using (var tail = connection.CreateCommand())
        {
            tail.Transaction = tx; tail.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
            Add(tail, "$tenant", tenant); await using var reader = await tail.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); }
            else { sequence = 1; previous = AuditLedgerHash.Genesis; }
        }
        var hash = AuditLedgerHash.Compute(previous, tenant, sequence, eventType, payload, at);
        await ExecuteAsync(connection, tx,
            "INSERT INTO audit_ledger (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES ($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);",
            token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$sequence", sequence),
            ("$previous", previous), ("$hash", hash), ("$type", eventType), ("$payload", payload), ("$at", Store(at)));
    }

    private static Task AppendOutboxAsync(SqliteConnection connection, SqliteTransaction tx,
        string tenant, string eventType, string payload, DateTimeOffset at, CancellationToken token) =>
        ExecuteAsync(connection, tx,
            "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($id,$tenant,$type,$payload,$at);",
            token, ("$id", UlidValue.New(at).ToString()), ("$tenant", tenant), ("$type", eventType),
            ("$payload", PersistenceSanitizer.SanitizeJson(payload)), ("$at", Store(at)));

    private static async Task<ProjectRecord?> ReadProjectAsync(SqliteConnection connection, SqliteTransaction? tx,
        string tenant, string id, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = $"{ProjectSelect} WHERE tenant_id=$tenant AND id=$id AND deleted_at IS NULL;";
        Add(query, "$tenant", tenant); Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9), reader.GetString(10), JsonSerializer.Deserialize<string[]>(reader.GetString(11), JsonOptions)!,
            new(reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15)),
            JsonSerializer.Deserialize<string[]>(reader.GetString(16), JsonOptions)!, reader.GetInt64(17), reader.GetString(18),
            reader.GetString(19), Parse(reader.GetString(20)), Parse(reader.GetString(21)), reader.GetInt64(22));
    }

    private static async Task<AgentRecord?> ReadAgentAsync(SqliteConnection connection, SqliteTransaction? tx,
        string tenant, string id, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = tx;
        query.CommandText = $"{AgentSelect} WHERE tenant_id=$tenant AND id=$id AND retired_at IS NULL;";
        Add(query, "$tenant", tenant); Add(query, "$id", id); await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var lease = reader.IsDBNull(8) ? null : new AgentLeaseRecord(reader.GetInt64(8), Parse(reader.GetString(9)));
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            lease, new(reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetDecimal(13), reader.GetInt64(14)),
            reader.IsDBNull(15) ? null : Parse(reader.GetString(15)));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction tx, string sql,
        CancellationToken token, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var value in values) Add(command, value.Name, value.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private const string ProjectSelect = "SELECT tenant_id,id,organization_id,name,project_key,description,state,criticality,repository_url,repository_provider,default_branch,technologies_json,logo_url,primary_color,secondary_color,typography,member_profile_ids_json,config_version,chief_agent_id,operation_mode,created_at,last_activity_at,version FROM projects";
    private const string AgentSelect = "SELECT tenant_id,id,definition_id,project_id,name,state,current_task_id,model_id,lease_fencing_token,lease_expires_at,tasks_completed,tokens_input,tokens_output,cost_usd,uptime_ms,last_heartbeat_at FROM agents";
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
}
