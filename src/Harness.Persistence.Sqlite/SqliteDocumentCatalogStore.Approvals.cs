using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Sqlite;

public sealed partial class SqliteDocumentCatalogStore
{
    public Task<ApprovalCatalogRecord> CreateGeneralApprovalAsync(
        GeneralApprovalCreateCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => CreateGeneralCoreAsync(connection, command, token),
            cancellationToken);

    public Task<ApprovalCatalogRecord?> ResolveGeneralApprovalAsync(
        GeneralApprovalResolveCommand command, CancellationToken cancellationToken = default) =>
        _dispatcher.ExecuteAsync((connection, token) => ResolveGeneralCoreAsync(connection, command, token),
            cancellationToken);

    private static async Task<ApprovalCatalogRecord> CreateGeneralCoreAsync(
        SqliteConnection connection, GeneralApprovalCreateCommand value, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await EnsureGeneralReferencesAsync(connection, transaction, value, token);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO general_approval_requests " +
                "(id,tenant_id,project_id,gate_id,task_id,title,description,priority,due_at,state," +
                "requested_by_agent_id,requested_at,version) VALUES " +
                "($id,$tenant,$project,$gate,$task,$title,$description,$priority,$due,'pending',$agent,$at,1);";
            Add(insert, "$id", value.Id); Add(insert, "$tenant", value.TenantId);
            Add(insert, "$project", value.ProjectId); AddNullable(insert, "$gate", value.GateId);
            AddNullable(insert, "$task", value.TaskId); Add(insert, "$title", value.Title);
            Add(insert, "$description", value.Description); Add(insert, "$priority", value.Priority);
            AddNullable(insert, "$due", value.DueAt is null ? null : Store(value.DueAt.Value));
            Add(insert, "$agent", value.RequestedByAgentId); Add(insert, "$at", Store(value.OccurredAt));
            await insert.ExecuteNonQueryAsync(token);
        }

        var record = new ApprovalCatalogRecord(value.Id, value.ProjectId, value.GateId, value.TaskId,
            null, value.Title, value.Description, value.Priority, value.DueAt, "pending",
            value.RequestedByAgentId, value.OccurredAt, null, null, null, 1);
        var payload = JsonSerializer.Serialize(new { projectId = value.ProjectId, approval = ToPayload(record) });
        await AppendEventAsync(connection, transaction, value.TenantId, "approval.requested", payload,
            value.OccurredAt, token);
        await transaction.CommitAsync(token);
        return record;
    }

    private static async Task<ApprovalCatalogRecord?> ResolveGeneralCoreAsync(
        SqliteConnection connection, GeneralApprovalResolveCommand value, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        ApprovalCatalogRecord? current;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT * FROM (" + ApprovalSelect +
                ") a WHERE tenant_id=$tenant AND id=$id AND document_id IS NULL;";
            Add(query, "$tenant", value.TenantId); Add(query, "$id", value.Id);
            await using var reader = await query.ExecuteReaderAsync(token);
            current = await reader.ReadAsync(token) ? ReadApproval(reader) : null;
        }
        if (current is null) { await transaction.CommitAsync(token); return null; }
        if (current.State != "pending")
            throw new InvalidOperationException("Approval is already resolved.");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE general_approval_requests SET state=$state,resolved_by_profile_id=$profile," +
                "resolved_at=$at,resolution_note=$note,version=version+1 WHERE tenant_id=$tenant AND id=$id AND state='pending';";
            Add(update, "$state", value.Decision); Add(update, "$profile", value.ResolvedByProfileId);
            Add(update, "$at", Store(value.OccurredAt)); AddNullable(update, "$note", value.Note);
            Add(update, "$tenant", value.TenantId); Add(update, "$id", value.Id);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Approval changed during resolution.");
        }

        GateResolutionRow? gateResolution = null;
        if (current.GateId is not null)
        {
            gateResolution = await EnsureGateCanResolveAsync(
                connection, transaction, value.TenantId, current.GateId, token);
            await using var gate = connection.CreateCommand(); gate.Transaction = transaction;
            gate.CommandText =
                "UPDATE workflow_gate_runs SET state=$state,decided_by_profile_id=$profile," +
                "evaluated_at=$at,decision_note=$note,version=version+1 WHERE tenant_id=$tenant AND id=$id AND state IN ('pending','failed');";
            Add(gate, "$state", value.Decision == "approved" ? "passed" : "failed");
            Add(gate, "$profile", value.ResolvedByProfileId); Add(gate, "$at", Store(value.OccurredAt));
            AddNullable(gate, "$note", value.Note); Add(gate, "$tenant", value.TenantId); Add(gate, "$id", current.GateId);
            if (await gate.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Gate cannot be resolved from its current state.");
            if (value.Decision == "approved")
            {
                await using var objective = connection.CreateCommand(); objective.Transaction = transaction;
                objective.CommandText = "UPDATE workflow_objective_runs SET state='approved',version=version+1 " +
                    "WHERE tenant_id=$tenant AND id=$id AND state<>'approved';";
                Add(objective, "$tenant", value.TenantId); Add(objective, "$id", gateResolution.ObjectiveRunId);
                await objective.ExecuteNonQueryAsync(token);
            }
        }

        var resolved = current with
        {
            State = value.Decision,
            ResolvedByProfileId = value.ResolvedByProfileId,
            ResolvedAt = value.OccurredAt,
            ResolutionNote = value.Note,
            AggregateVersion = current.AggregateVersion + 1
        };
        var payload = JsonSerializer.Serialize(new
        {
            projectId = current.ProjectId,
            approvalId = value.Id,
            state = value.Decision,
            resolvedByProfileId = value.ResolvedByProfileId,
            note = value.Note
        });
        await AppendEventAsync(connection, transaction, value.TenantId, "approval.resolved", payload,
            value.OccurredAt, token);
        if (current.GateId is not null)
        {
            var gatePayload = JsonSerializer.Serialize(new
            {
                projectId = current.ProjectId,
                gateId = current.GateId,
                runId = gateResolution!.RunId,
                from = "pending",
                to = value.Decision,
                decidedByProfileId = value.ResolvedByProfileId,
                note = value.Note
            });
            await InsertOutboxAsync(connection, transaction, value.TenantId, "gate.changed", gatePayload,
                value.OccurredAt.AddTicks(1), token);
        }
        await transaction.CommitAsync(token);
        return resolved;
    }

    private static async Task EnsureGeneralReferencesAsync(SqliteConnection connection,
        SqliteTransaction transaction, GeneralApprovalCreateCommand value, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText =
            "SELECT EXISTS(SELECT 1 FROM projects WHERE tenant_id=$tenant AND id=$project AND deleted_at IS NULL)," +
            "($task IS NULL OR EXISTS(SELECT 1 FROM work_tasks WHERE tenant_id=$tenant AND project_id=$project AND id=$task))," +
            "($gate IS NULL OR EXISTS(SELECT 1 FROM workflow_gate_runs g JOIN workflow_phase_runs p ON p.id=g.phase_run_id " +
            "JOIN workflow_runs r ON r.id=p.workflow_run_id WHERE g.tenant_id=$tenant AND g.id=$gate AND r.project_id=$project));";
        Add(query, "$tenant", value.TenantId); Add(query, "$project", value.ProjectId);
        AddNullable(query, "$task", value.TaskId); AddNullable(query, "$gate", value.GateId);
        await using var reader = await query.ExecuteReaderAsync(token); await reader.ReadAsync(token);
        if (reader.GetInt64(0) == 0) throw new ApprovalReferenceNotFoundException("project");
        if (reader.GetInt64(1) == 0) throw new ApprovalReferenceNotFoundException("task");
        if (reader.GetInt64(2) == 0) throw new ApprovalReferenceNotFoundException("gate");
    }

    private static async Task<GateResolutionRow> EnsureGateCanResolveAsync(
        SqliteConnection connection, SqliteTransaction transaction, string tenantId,
        string gateId, CancellationToken token)
    {
        await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText =
            "SELECT p.workflow_run_id,o.id,d.id,d.minimum_required_state,p.id " +
            "FROM workflow_gate_runs g JOIN workflow_phase_runs p ON p.id=g.phase_run_id " +
            "JOIN workflow_runs w ON w.id=p.workflow_run_id " +
            "JOIN workflow_gate_definitions d ON d.id=g.gate_definition_id " +
            "JOIN workflow_objective_runs o ON o.phase_run_id=p.id " +
            "AND o.objective_definition_id=d.objective_definition_id " +
            "WHERE g.tenant_id=$tenant AND g.id=$id AND g.state IN ('pending','failed') " +
            "AND p.state='active' AND w.state='running';";
        Add(query, "$tenant", tenantId); Add(query, "$id", gateId);
        string runId; string objectiveRunId; string definitionId; string minimum; string phaseRunId;
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token))
                throw new InvalidOperationException("Gate is not active for resolution.");
            runId = reader.GetString(0); objectiveRunId = reader.GetString(1);
            definitionId = reader.GetString(2); minimum = reader.GetString(3); phaseRunId = reader.GetString(4);
        }
        await using var requirements = connection.CreateCommand(); requirements.Transaction = transaction;
        requirements.CommandText =
            "SELECT o.state FROM workflow_gate_requirements r JOIN workflow_objective_runs o " +
            "ON o.objective_definition_id=r.objective_definition_id AND o.phase_run_id=$phase " +
            "WHERE r.gate_definition_id=$gate ORDER BY r.requirement_order;";
        Add(requirements, "$phase", phaseRunId); Add(requirements, "$gate", definitionId);
        await using var states = await requirements.ExecuteReaderAsync(token);
        while (await states.ReadAsync(token))
            if (StateRank(states.GetString(0)) < StateRank(minimum))
                throw new InvalidOperationException("Gate requirements are not met.");
        return new(runId, objectiveRunId);
    }

    private static int StateRank(string state) => state switch
    {
        "pending" => 0,
        "executed" => 1,
        "validated" => 2,
        "approved" => 3,
        _ => throw new InvalidOperationException("Objective state is invalid."),
    };

    private static async Task AppendEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        string tenantId, string eventType, string payload, DateTimeOffset occurredAt, CancellationToken token)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        long sequence; string previous;
        await using (var tail = connection.CreateCommand())
        {
            tail.Transaction = transaction; tail.CommandText =
                "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
            Add(tail, "$tenant", tenantId); await using var reader = await tail.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); }
            else { sequence = 1; previous = new string('0', 64); }
        }
        var hash = AuditLedgerHash.Compute(previous, tenantId, sequence, eventType, payload, occurredAt);
        await using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction; ledger.CommandText =
                "INSERT INTO audit_ledger (id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) " +
                "VALUES ($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);";
            Add(ledger, "$id", UlidValue.New(occurredAt).ToString()); Add(ledger, "$tenant", tenantId);
            Add(ledger, "$sequence", sequence); Add(ledger, "$previous", previous); Add(ledger, "$hash", hash);
            Add(ledger, "$type", eventType); Add(ledger, "$payload", payload); Add(ledger, "$at", Store(occurredAt));
            await ledger.ExecuteNonQueryAsync(token);
        }
        await InsertOutboxAsync(connection, transaction, tenantId, eventType, payload, occurredAt, token);
    }

    private static async Task InsertOutboxAsync(SqliteConnection connection, SqliteTransaction transaction,
        string tenantId, string eventType, string payload, DateTimeOffset occurredAt, CancellationToken token)
    {
        await using var outbox = connection.CreateCommand(); outbox.Transaction = transaction;
        outbox.CommandText = "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) " +
            "VALUES ($id,$tenant,$type,$payload,$at);";
        Add(outbox, "$id", UlidValue.New(occurredAt).ToString()); Add(outbox, "$tenant", tenantId);
        Add(outbox, "$type", eventType); Add(outbox, "$payload", payload); Add(outbox, "$at", Store(occurredAt));
        await outbox.ExecuteNonQueryAsync(token);
    }

    private static object ToPayload(ApprovalCatalogRecord value) => new
    {
        id = value.Id,
        projectId = value.ProjectId,
        gateId = value.GateId,
        taskId = value.TaskId,
        documentId = value.DocumentId,
        title = value.Title,
        description = value.Description,
        priority = value.Priority,
        dueAt = value.DueAt,
        state = value.State,
        requestedByAgentId = value.RequestedByAgentId,
        requestedAt = value.RequestedAt,
        resolvedByProfileId = value.ResolvedByProfileId,
        resolvedAt = value.ResolvedAt,
        resolutionNote = value.ResolutionNote,
    };

    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private sealed record GateResolutionRow(string RunId, string ObjectiveRunId);
}
