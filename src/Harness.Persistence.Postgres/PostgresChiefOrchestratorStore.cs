using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed class PostgresChiefOrchestratorStore(NpgsqlDataSource dataSource) : IChiefOrchestratorStore
{
    private const string ProjectSelect =
        """
        SELECT tenant_id,id,organization_id,name,project_key,description,state,criticality,
               repository_url,repository_provider,default_branch,technologies_json::text,logo_url,
               primary_color,secondary_color,typography,member_profile_ids_json::text,config_version,
               chief_agent_id,operation_mode,created_at,COALESCE(last_activity_at,created_at),version
        FROM harness.projects
        """;

    private const string AgentSelect =
        "SELECT tenant_id,id,definition_id,project_id,name,state,current_task_id,model_id," +
        "lease_fencing_token,lease_expires_at,tasks_completed,tokens_input,tokens_output," +
        "cost_usd,uptime_ms,last_heartbeat_at FROM harness.agents";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<ProjectRecord> PauseAsync(
        ChiefProjectCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetPauseStateAsync(command, pause: true, cancellationToken);
    }

    public Task<ProjectRecord> ResumeAsync(
        ChiefProjectCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetPauseStateAsync(command, pause: false, cancellationToken);
    }

    public Task<AgentRecord> HandoffAsync(
        ChiefHandoffCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return HandoffCoreAsync(command, cancellationToken);
    }

    public Task<int> DrainAsync(
        ChiefDrainCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DrainCoreAsync(command, cancellationToken);
    }

    private static async Task<int> CountRunningAttemptsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChiefProjectCommand command,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT COUNT(*) FROM harness.work_attempts WHERE tenant_id=$1 AND project_id=$2 AND operational_state='running';";
        query.Parameters.Add(Text(command.TenantId));
        query.Parameters.Add(Text(command.ProjectId));
        return Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<ProjectRecord> SetPauseStateAsync(
        ChiefProjectCommand command, bool pause, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var project = await ReadProjectAsync(
                connection, transaction, command.TenantId, command.ProjectId, cancellationToken)
            ?? throw new ChiefResourceNotFoundException("project");
        if (project.State == "archived")
        {
            throw new ChiefStateConflictException("An archived project cannot change orchestration state.");
        }

        var chief = await ReadAgentAsync(
                connection, transaction, command.TenantId, project.ChiefAgentId, cancellationToken)
            ?? throw new ChiefResourceNotFoundException("chief_agent");
        var projectState = pause ? "paused" : "active";
        var agentState = pause ? "waiting" : "idle";
        if (project.State == projectState && chief.State == agentState)
        {
            await transaction.CommitAsync(cancellationToken);
            return project;
        }

        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.projects SET state=$1,last_activity_at=$2,version=version+1 WHERE tenant_id=$3 AND id=$4 AND deleted_at IS NULL;",
            cancellationToken,
            Text(projectState), Timestamp(command.OccurredAt),
            Text(command.TenantId), Text(command.ProjectId));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.agents SET state=$1,current_task_id=NULL,last_heartbeat_at=$2 WHERE tenant_id=$3 AND id=$4;",
            cancellationToken,
            Text(agentState), Timestamp(command.OccurredAt),
            Text(command.TenantId), Text(chief.Id));
        if (chief.State != agentState)
        {
            await AppendAgentEventAsync(
                connection, transaction, command.TenantId, chief.Id, chief.State, agentState,
                null, command.OccurredAt, cancellationToken);
        }

        var action = pause ? "chief.paused" : "chief.resumed";
        // Pausar interrompe o DESPACHO; não alcança a tentativa que já está em execução (para
        // isso existe o drain). Silenciar essa diferença fez a pausa de 2026-08-03 parecer
        // total enquanto duas tentativas seguiam vivas segurando a única conta disponível.
        var inFlight = pause
            ? await CountRunningAttemptsAsync(connection, transaction, command, cancellationToken)
            : 0;
        var detail = pause
            ? inFlight == 0
                ? "Project orchestration paused."
                : $"Project orchestration paused. {inFlight} attempt(s) already running continue until they finish; use drain to cancel them."
            : "Project orchestration resumed.";
        await AppendAuditEventAsync(
            connection, transaction, command.TenantId, command.ProjectId,
            command.ActorProfileId, action, detail, command.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadProjectAsync(
                connection, null, command.TenantId, command.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException("The project disappeared after the orchestration update.");
    }

    private async Task<AgentRecord> HandoffCoreAsync(
        ChiefHandoffCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var project = await ReadProjectAsync(
                connection, transaction, command.TenantId, command.ProjectId, cancellationToken)
            ?? throw new ChiefResourceNotFoundException("project");
        if (project.State == "archived")
        {
            throw new ChiefStateConflictException("An archived project cannot hand off orchestration.");
        }

        var oldChief = await ReadAgentAsync(
                connection, transaction, command.TenantId, project.ChiefAgentId, cancellationToken)
            ?? throw new ChiefResourceNotFoundException("chief_agent");
        var definitionId = command.TargetDefinitionId ?? oldChief.DefinitionId;
        await using (var definition = connection.CreateCommand())
        {
            definition.Transaction = transaction;
            definition.CommandText = "SELECT role FROM harness.agent_definitions WHERE id=$1;";
            definition.Parameters.Add(Text(definitionId));
            var role = await definition.ExecuteScalarAsync(cancellationToken) as string;
            if (role is null)
            {
                throw new ChiefResourceNotFoundException("agent_definition");
            }

            if (role != "chief")
            {
                throw new ChiefStateConflictException("The target definition must have the chief role.");
            }
        }

        if (command.TargetModelId is not null)
        {
            await using var model = connection.CreateCommand();
            model.Transaction = transaction;
            model.CommandText = "SELECT enabled FROM harness.provider_models WHERE tenant_id=$1 AND id=$2;";
            model.Parameters.Add(Text(command.TenantId));
            model.Parameters.Add(Text(command.TargetModelId));
            var enabled = await model.ExecuteScalarAsync(cancellationToken);
            if (enabled is null)
            {
                throw new ChiefResourceNotFoundException("model");
            }

            if (!(bool)enabled)
            {
                throw new ChiefStateConflictException("The target model is disabled.");
            }
        }

        long fencing;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT COALESCE(MAX(lease_fencing_token),0)+1 FROM harness.agents WHERE tenant_id=$1 AND project_id=$2;";
            query.Parameters.Add(Text(command.TenantId));
            query.Parameters.Add(Text(command.ProjectId));
            fencing = (long)(await query.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL did not return a fencing token."));
        }

        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.agents SET state='idle',current_task_id=NULL,lease_fencing_token=NULL,lease_expires_at=NULL WHERE tenant_id=$1 AND id=$2;",
            cancellationToken,
            Text(command.TenantId), Text(oldChief.Id));
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.agents
                (id,tenant_id,definition_id,project_id,name,state,current_task_id,model_id,
                 lease_fencing_token,lease_expires_at,last_heartbeat_at,created_at)
            VALUES ($1,$2,$3,$4,$5,'idle',NULL,$6,$7,$8,$9,$9);
            """,
            cancellationToken,
            Text(command.NewAgentId), Text(command.TenantId), Text(definitionId),
            Text(command.ProjectId), Text(oldChief.Name), NullableText(command.TargetModelId),
            Bigint(fencing), Timestamp(command.OccurredAt.AddMinutes(1)),
            Timestamp(command.OccurredAt));
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.projects SET chief_agent_id=$1,last_activity_at=$2,version=version+1 WHERE tenant_id=$3 AND id=$4 AND deleted_at IS NULL;",
            cancellationToken,
            Text(command.NewAgentId), Timestamp(command.OccurredAt),
            Text(command.TenantId), Text(command.ProjectId));
        await AppendAgentEventAsync(
            connection, transaction, command.TenantId, oldChief.Id, oldChief.State, "idle",
            null, command.OccurredAt, cancellationToken);
        await AppendAgentEventAsync(
            connection, transaction, command.TenantId, command.NewAgentId, "idle", "idle",
            null, command.OccurredAt.AddTicks(1), cancellationToken);
        await AppendAuditEventAsync(
            connection, transaction, command.TenantId, command.ProjectId,
            command.ActorProfileId, "chief.handedOff",
            $"Chief handoff completed with fencing {fencing}. Reason: {command.Note}",
            command.OccurredAt.AddTicks(2), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadAgentAsync(
                connection, null, command.TenantId, command.NewAgentId, cancellationToken)
            ?? throw new InvalidOperationException("The new chief agent disappeared after the handoff.");
    }

    private async Task<int> DrainCoreAsync(
        ChiefDrainCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        _ = await ReadProjectAsync(
                connection, transaction, command.TenantId, command.ProjectId, cancellationToken)
            ?? throw new ChiefResourceNotFoundException("project");
        var tasks = new List<(string Id, string State)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT id,board_state FROM harness.work_tasks WHERE tenant_id=$1 AND project_id=$2 " +
                "AND board_state IN ('development','review','corrections','testsGates') ORDER BY id FOR UPDATE;";
            query.Parameters.Add(Text(command.TenantId));
            query.Parameters.Add(Text(command.ProjectId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tasks.Add((reader.GetString(0).TrimEnd(), reader.GetString(1)));
            }
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        foreach (var task in tasks)
        {
            await ExecuteAsync(
                connection, transaction,
                "UPDATE harness.work_tasks SET state='ready',board_state='ready',blocked_reason=NULL,version=version+1,updated_at=$1 WHERE tenant_id=$2 AND id=$3;",
                cancellationToken,
                Timestamp(command.OccurredAt), Text(command.TenantId), Text(task.Id));
            var payload = JsonSerializer.Serialize(new
            {
                projectId = command.ProjectId,
                taskId = task.Id,
                from = task.State,
                to = "ready",
                changedByKind = "chief",
                note,
            }, JsonOptions);
            await AppendLedgerAsync(
                connection, transaction, command.TenantId, "task.stateChanged", payload,
                command.OccurredAt, cancellationToken);
            await AppendOutboxAsync(
                connection, transaction, command.TenantId, "task.stateChanged", payload,
                command.OccurredAt, cancellationToken);
        }

        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.work_attempts SET state='rejected',operational_state='cancelled',completed_at=$1," +
            "duration_ms=GREATEST(0,trunc(EXTRACT(EPOCH FROM ($1 - started_at))*1000)::bigint)," +
            "failure_reason='Cancelled by Chief drain.' WHERE tenant_id=$2 AND project_id=$3 AND operational_state='running';",
            cancellationToken,
            Timestamp(command.OccurredAt), Text(command.TenantId), Text(command.ProjectId));

        var agents = new List<(string Id, string State)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT id,state FROM harness.agents WHERE tenant_id=$1 AND project_id=$2 " +
                "AND retired_at IS NULL AND state IN ('working','waiting') ORDER BY id FOR UPDATE;";
            query.Parameters.Add(Text(command.TenantId));
            query.Parameters.Add(Text(command.ProjectId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                agents.Add((reader.GetString(0).TrimEnd(), reader.GetString(1)));
            }
        }

        foreach (var agent in agents)
        {
            await ExecuteAsync(
                connection, transaction,
                "UPDATE harness.agents SET state='idle',current_task_id=NULL,last_heartbeat_at=$1 WHERE tenant_id=$2 AND id=$3;",
                cancellationToken,
                Timestamp(command.OccurredAt), Text(command.TenantId), Text(agent.Id));
            await AppendAgentEventAsync(
                connection, transaction, command.TenantId, agent.Id, agent.State, "idle",
                null, command.OccurredAt, cancellationToken);
        }

        await CancelDurableExecutionsAsync(connection, transaction, command, cancellationToken);
        await ExecuteAsync(
            connection, transaction,
            "UPDATE harness.projects SET last_activity_at=$1,version=version+1 WHERE tenant_id=$2 AND id=$3 AND deleted_at IS NULL;",
            cancellationToken,
            Timestamp(command.OccurredAt), Text(command.TenantId), Text(command.ProjectId));
        await AppendAuditEventAsync(
            connection, transaction, command.TenantId, command.ProjectId,
            command.ActorProfileId, "chief.tasksDrained",
            $"{tasks.Count} task(s) drained to ready.{(note is null ? string.Empty : $" Note: {note}")}",
            command.OccurredAt.AddTicks(3), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tasks.Count;
    }

    private static async Task CancelDurableExecutionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChiefDrainCommand command,
        CancellationToken cancellationToken)
    {
        var executions = new List<(string Id, string State, string? AttemptId)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT id,state,active_attempt_id FROM harness.durable_executions WHERE tenant_id=$1 AND project_id=$2 " +
                "AND state NOT IN ('completed','cancelled','dead_letter') ORDER BY id FOR UPDATE;";
            query.Parameters.Add(Text(command.TenantId));
            query.Parameters.Add(Text(command.ProjectId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                executions.Add((
                    reader.GetString(0).TrimEnd(),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2).TrimEnd()));
            }
        }

        foreach (var execution in executions)
        {
            if (execution.AttemptId is not null)
            {
                await ExecuteAsync(
                    connection, transaction,
                    "UPDATE harness.durable_attempts SET state='cancelled',ended_at=$1,error_code='chief_drain',error_detail='Cancelled by Chief drain.',version=version+1 WHERE id=$2 AND state='running';",
                    cancellationToken,
                    Timestamp(command.OccurredAt), Text(execution.AttemptId));
            }

            await ExecuteAsync(
                connection, transaction,
                "UPDATE harness.durable_executions SET state='cancelled',active_attempt_id=NULL,fencing_token=fencing_token+1,last_error='chief_drain',version=version+1,updated_at=$1 WHERE id=$2;",
                cancellationToken,
                Timestamp(command.OccurredAt), Text(execution.Id));
            long sequence;
            await using (var sequenceQuery = connection.CreateCommand())
            {
                sequenceQuery.Transaction = transaction;
                sequenceQuery.CommandText =
                    "SELECT COALESCE(MAX(sequence),0)+1 FROM harness.durable_transitions WHERE execution_id=$1;";
                sequenceQuery.Parameters.Add(Text(execution.Id));
                sequence = (long)(await sequenceQuery.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("PostgreSQL did not return a transition sequence."));
            }

            var payload = JsonSerializer.Serialize(
                new
                {
                    executionId = execution.Id,
                    attemptId = execution.AttemptId,
                    state = "cancelled",
                    reason = "chief-drain",
                },
                JsonOptions);
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.durable_transitions
                    (execution_id,sequence,from_state,to_state,reason,attempt_id,occurred_at)
                VALUES ($1,$2,$3,'cancelled','chief-drain',$4,$5);
                """,
                cancellationToken,
                Text(execution.Id), Bigint(sequence), Text(execution.State),
                NullableText(execution.AttemptId), Timestamp(command.OccurredAt));
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO harness.durable_execution_outbox
                    (execution_id,transition_sequence,event_type,payload_json,occurred_at)
                VALUES ($1,$2,'durable.stateChanged',$3,$4);
                """,
                cancellationToken,
                Text(execution.Id), Bigint(sequence), Json(payload),
                Timestamp(command.OccurredAt));
            await AppendLedgerAsync(
                connection, transaction, command.TenantId, "durable.stateChanged", payload,
                command.OccurredAt, cancellationToken);
        }
    }

    private static async Task AppendAgentEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string agentId,
        string from,
        string to,
        string? currentTaskId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { agentId, from, to, currentTaskId }, JsonOptions);
        await AppendOutboxAsync(
            connection, transaction, tenantId, "agent.statusChanged", payload, at, cancellationToken);
    }

    private static async Task AppendAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string actorProfileId,
        string action,
        string detail,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var id = UlidValue.New(at).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            projectId,
            auditEvent = new
            {
                id,
                actorKind = "user",
                actorId = actorProfileId,
                action,
                targetType = "project",
                targetId = projectId,
                detail,
                occurredAt = at,
            },
        }, JsonOptions);
        await AppendLedgerAsync(connection, transaction, tenantId, action, payload, at, cancellationToken);
        await AppendOutboxAsync(
            connection, transaction, tenantId, "audit.eventAppended", payload, at, cancellationToken);
    }

    private static async Task AppendLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        await ExecuteAsync(
            connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenantId}"));
        var (sequence, previousHash) = await ReadLedgerTailAsync(
            connection, transaction, tenantId, cancellationToken);
        var hash = AuditLedgerHash.Compute(previousHash, tenantId, sequence, eventType, payload, at);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO harness.audit_ledger
                (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
            """,
            cancellationToken,
            Text(UlidValue.New(at).ToString()), Text(tenantId), Bigint(sequence),
            Text(previousHash), Text(hash), Text(eventType), Json(payload), Timestamp(at));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection, transaction,
            "INSERT INTO harness.outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) VALUES ($1,$2,$3,$4,$5);",
            cancellationToken,
            Text(UlidValue.New(at).ToString()), Text(tenantId), Text(eventType),
            Json(PersistenceSanitizer.SanitizeJson(payload)), Timestamp(at));

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenantId));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task<ProjectRecord?> ReadProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{ProjectSelect} WHERE tenant_id=$1 AND id=$2 AND deleted_at IS NULL" +
            (transaction is null ? ";" : " FOR UPDATE;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(projectId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProject(reader) : null;
    }

    private static ProjectRecord ReadProject(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            Deserialize(reader.GetString(11)),
            new ProjectBrandRecord(
                Nullable(reader, 12),
                Nullable(reader, 13),
                Nullable(reader, 14),
                Nullable(reader, 15)),
            Deserialize(reader.GetString(16)),
            reader.GetInt64(17),
            reader.GetString(18).TrimEnd(),
            reader.GetString(19),
            reader.GetFieldValue<DateTimeOffset>(20),
            reader.GetFieldValue<DateTimeOffset>(21),
            reader.GetInt64(22));

    private static async Task<AgentRecord?> ReadAgentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            $"{AgentSelect} WHERE tenant_id=$1 AND id=$2 AND retired_at IS NULL" +
            (transaction is null ? ";" : " FOR UPDATE;");
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(agentId));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAgent(reader) : null;
    }

    private static AgentRecord ReadAgent(NpgsqlDataReader reader)
    {
        var lease = reader.IsDBNull(8)
            ? null
            : new AgentLeaseRecord(reader.GetInt64(8), reader.GetFieldValue<DateTimeOffset>(9));
        return new AgentRecord(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.GetString(2).TrimEnd(),
            reader.IsDBNull(3) ? null : reader.GetString(3).TrimEnd(),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6).TrimEnd(),
            reader.IsDBNull(7) ? null : reader.GetString(7).TrimEnd(),
            lease,
            new AgentMetricsRecord(
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetDecimal(13),
                reader.GetInt64(14)),
            reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string[] Deserialize(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("Persisted project JSON cannot be null.");

    private static string? Nullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

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
